#!/usr/bin/python3
"""Keep one shairport-sync instance per AirPlay destination and report its sessions."""

import json
import os
from pathlib import Path
import queue
import selectors
import signal
import socket
import stat
import subprocess
import threading
import time
import uuid

from casting_sessions import (RELAY_SOCKET, SAMPLE_FORMAT, AdapterEvent, AirPlayAdapter, CastingJournal, CastingMapper,
                              CastingSpec, MetadataParser, TraceFormatter, allocate_slots, casting_destinations,
                              display_name, instance_command, render_config, unique_names)
from spotify_sessions import atomic_json, read_json, receiver_identity
from spotify_supervisor import HEARTBEAT_SECONDS, PREFIX, REPUBLISH_SECONDS, SnapControl, private_directory, stop

RECONCILE_SECONDS = 1
RECONCILE_RETRY_LIMIT = 10
RESTART_DELAYS = (1, 2, 4, 8, 16, 30)
STABLE_SECONDS = 60
DBUS_RETRY_SECONDS = 30
AVAHI_RECOVERY_SECONDS = 5
NQPTP_WAIT_SECONDS = 1.0
NQPTP_RETRY_SECONDS = 10
STOP_SECONDS = 2
PUBLISH_WAIT_SECONDS = 1.5
STDERR_LIMIT = 64 * 1024
# The patched shairport-sync sends this ssnc item when it loses Avahi, and a sender cannot forge a metadata code.
AVAHI_LOST = 'syav'


def relay_check(socket_path=RELAY_SOCKET):
    from casting_dbus_relay import check_avahi
    return check_avahi(socket_path)


def atomic_text(path, text):
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex)
    descriptor = os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    try:
        with os.fdopen(descriptor, 'w') as stream:
            stream.write(text)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def log(message):
    print(message, flush=True)


class CastingSupervisor:
    def __init__(self, root, runtime, publish, snapcast=None, connected=lambda: True, clock=time.monotonic,
                 avahi_check=relay_check, shm_directory='/dev/shm', nqptp_address=('127.0.0.1', 9000), trace=False,
                 background=lambda function: threading.Thread(target=function, daemon=True).start()):
        self.root = private_directory(Path(root))
        self.runtime = private_directory(Path(runtime))
        self.publish = publish
        self.connected = connected
        self.clock = clock
        self.avahi_check = avahi_check
        self.background = background
        self.shm_directory = Path(shm_directory)
        self.nqptp_address = nqptp_address
        self.trace = TraceFormatter() if trace else None
        self.snapcast = snapcast or SnapControl()
        self.journal = CastingJournal(self.root / 'lifecycle.json')
        self.slots = read_json(self.root / 'slots.json', {})
        self.configuration = None
        self.catalogue = None
        self.reconciled_generation = 0
        self.targets = {}
        self.incoming = queue.SimpleQueue()
        self.output = queue.SimpleQueue()
        self.stopped = threading.Event()
        self.reader_stopped = threading.Event()
        self.sent = {}
        self.notes = {}
        self.stderr_buffers = {}
        self.next_reconcile = 0
        self.reconcile_delay = RECONCILE_SECONDS
        self.heartbeats = ()
        self.dbus_ok = False
        self.dbus_next_check = 0
        self.dbus_checking = False
        self.dbus_results = queue.SimpleQueue()
        self.nqptp_retry = 0
        self.region_requests = {}
        self.stopping = []
        self.withdrawn = False
        self.cleaned = False
        self.tokens = 0
        self.watch_lock = threading.Lock()
        self.watch_count = 0
        self.watches = {}
        self.closing = []
        self.wake_reader, self.wake_writer = os.pipe()
        os.set_blocking(self.wake_reader, False)
        os.set_blocking(self.wake_writer, False)
        for child in self.runtime.iterdir():
            if child.name.startswith('airplay-') and ('.conf' in child.name or child.name.endswith('.meta')):
                child.unlink(missing_ok=True)
        self.end_leftover_sessions()

    def end_leftover_sessions(self):
        # A shairport-sync restart cannot keep an AirPlay connection, so the last run's sessions end now.
        for identity, receiver in list(self.journal.state['receivers'].items()):
            if receiver['sessionId'] is None:
                continue
            entry = next((entries[identity] for entries in self.slots.values()
                          if isinstance(entries, dict) and isinstance(entries.get(identity), dict)), None)
            if entry and 'destination' in entry:
                self.journal.emit(CastingSpec(identity, entry['destination'], entry.get('name', ''),
                                              entry.get('slot', 0), self.runtime), 'end')
            else:
                self.journal.abandon(receiver['sessionId'], identity)

    def note(self, key, message):
        if self.notes.get(key) != message:
            self.notes[key] = message
            log(message)

    def failed(self, identity, error):
        self.note(identity + ':error', f'AirPlay destination {identity[:12]} failed: {type(error).__name__}')

    def wanted(self):
        state_id = self.configuration['stateId']
        candidates = [(receiver_identity(state_id, 'casting:airplay', destination), destination, display_name(name))
                      for destination, name in casting_destinations(self.configuration)]
        slots, skipped = allocate_slots(self.slots, state_id, candidates)
        if slots != self.slots:
            self.slots = slots
            atomic_json(self.root / 'slots.json', slots)
        for identity in skipped:
            self.note(identity + ':slot', f'AirPlay destination {identity[:12]} skipped: all 16 slots are in use')
        # Slot order keeps the plain name with the same destination on every start.
        entries = sorted(slots[state_id].items(), key=lambda pair: pair[1]['slot'])
        names = unique_names([entry['name'] for identity, entry in entries])
        return {identity: CastingSpec(identity, entry['destination'], name, entry['slot'], self.runtime)
                for (identity, entry), name in zip(entries, names)}

    def region(self, specification):
        return self.shm_directory / specification.shm_name.lstrip('/')

    def add_target(self, specification):
        # Holding the audio FIFO open for reading and writing means shairport-sync always finds a reader and Snapserver never sees EOF.
        fifo = specification.fifo
        try:
            mode = fifo.lstat().st_mode
        except FileNotFoundError:
            mode = None
        if mode is not None and not stat.S_ISFIFO(mode):
            fifo.unlink()
            mode = None
        if mode is None:
            os.mkfifo(fifo, 0o600)
        audio = os.open(fifo, os.O_RDWR | os.O_NONBLOCK)
        target = {'specification': specification, 'audio': audio, 'process': None, 'token': None, 'watches': (),
                  'mapper': CastingMapper(self.journal, specification, clock=self.clock, log=log),
                  'adapter': None, 'parser': None, 'started': 0, 'inode': None, 'failures': 0, 'next_start': 0,
                  'unadvertised': False}
        self.targets[specification.identity] = target
        return target

    def remove_target(self, identity, streams):
        target = self.targets.pop(identity)
        specification = target['specification']
        try:
            self.halt(target, self.clock())
            self.region_requests.pop(specification.slot, None)
            if specification.stream in streams:
                self.snapcast.request('Stream.RemoveStream', {'id': specification.stream})
                streams.discard(specification.stream)
        finally:
            os.close(target['audio'])
            for path in (specification.fifo, specification.meta, specification.config):
                path.unlink(missing_ok=True)

    def halt(self, target, now):
        target['mapper'].stop()
        process, specification = target['process'], target['specification']
        if process is not None and process.poll() is None:
            # The loop never waits for an instance to exit, and its slot and name stay taken until it has.
            process.terminate()
            self.stopping.append({'process': process, 'deadline': now + STOP_SECONDS, 'slot': specification.slot,
                                  'name': specification.name.lower()})
        for identifier in target['watches']:
            self.unwatch(identifier)
        if target['token'] is not None:
            # Its log pipe closes before EOF is read, so a half line is written out now.
            self.instance_log(specification.identity, target['token'], specification.slot, b'')
        target.update(process=None, token=None, watches=(), inode=None, unadvertised=False, next_start=now)

    def reap(self, now):
        # A halted instance that outlives its stop time is killed.
        for entry in list(self.stopping):
            if entry['process'].poll() is not None:
                self.stopping.remove(entry)
            elif now >= entry['deadline']:
                entry['process'].kill()
                entry['deadline'] = float('inf')

    def exited(self, target, code, now):
        specification = target['specification']
        if now - target['started'] >= STABLE_SECONDS:
            target['failures'] = 0
            self.notes.pop(specification.identity + ':exit', None)
        delay = RESTART_DELAYS[min(target['failures'], len(RESTART_DELAYS) - 1)]
        target['failures'] += 1
        self.note(specification.identity + ':exit', f'AirPlay instance {specification.slot} exited with {code}')
        self.halt(target, now)
        target['next_start'] = now + delay

    def check_dbus(self):
        # The relay can take 2 s to answer, so this runs off the main loop and hands back its result.
        version = None
        try:
            version = self.avahi_check()
        except ImportError:
            pass
        finally:
            self.dbus_results.put(version)

    def dbus_ready(self, now):
        if self.dbus_ok:
            return True
        if not self.dbus_checking and now >= self.dbus_next_check:
            self.dbus_checking = True
            self.background(self.check_dbus)
        try:
            version = self.dbus_results.get_nowait()
        except queue.Empty:
            return False
        self.dbus_checking = False
        if version:
            self.dbus_ok = True
            self.note('dbus', f'Casting D-Bus check passed: {version}')
            return True
        recovering = any(target['unadvertised'] for target in self.targets.values())
        self.dbus_next_check = now + (AVAHI_RECOVERY_SECONDS if recovering else DBUS_RETRY_SECONDS)
        self.note('dbus', 'Casting D-Bus check failed; AirPlay targets wait for the Avahi relay')
        return False

    def region_ready(self, specification, now):
        # nqptp creates the region when it first hears its name, and the trailing NUL matters.
        slot = specification.slot
        if slot not in self.region_requests:
            if now < self.nqptp_retry:
                return None
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as control:
                    control.sendto(specification.shm_name.encode() + b' T\0', self.nqptp_address)
            except OSError:
                pass
            self.region_requests[slot] = now
        try:
            inode = os.stat(self.region(specification)).st_ino
        except FileNotFoundError:
            # A later pass looks again, so the loop never waits for nqptp.
            if now - self.region_requests[slot] >= NQPTP_WAIT_SECONDS:
                del self.region_requests[slot]
                self.nqptp_retry = now + NQPTP_RETRY_SECONDS
                self.note('nqptp', 'nqptp did not create its shared memory; AirPlay targets wait and retry every 10 s')
            return None
        del self.region_requests[slot]
        self.notes.pop('nqptp', None)
        return inode

    def region_current(self, target):
        try:
            return os.stat(self.region(target['specification'])).st_ino == target['inode']
        except FileNotFoundError:
            return False

    def start_instance(self, target, now):
        specification = target['specification']
        if not self.dbus_ready(now):
            return
        inode = self.region_ready(specification, now)
        if inode is None:
            return
        # A new metadata FIFO per instance keeps a dead instance's items away from the next one.
        specification.meta.unlink(missing_ok=True)
        os.mkfifo(specification.meta, 0o600)
        atomic_text(specification.config, render_config(specification))
        metadata_pipe = os.open(specification.meta, os.O_RDWR | os.O_NONBLOCK)
        try:
            process = subprocess.Popen(instance_command(specification),
                                       env=dict(os.environ, DBUS_SYSTEM_BUS_ADDRESS='unix:path=' + RELAY_SOCKET),
                                       stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        except BaseException:
            os.close(metadata_pipe)
            raise
        error_pipe = os.dup(process.stderr.fileno())
        process.stderr.close()
        os.set_blocking(error_pipe, False)
        self.tokens += 1
        token = self.tokens
        target.update(process=process, token=token, started=now, inode=inode, adapter=AirPlayAdapter(),
                      parser=MetadataParser(), watches=(self.watch(metadata_pipe, specification, token, 'metadata'),
                                                        self.watch(error_pipe, specification, token, 'stderr')))

    def supervise(self, target, specification, status, now):
        mapper = target['mapper']
        process = target['process']
        if process is not None and process.poll() is not None:
            self.exited(target, process.returncode, now)
        elif process is not None and not self.region_current(target):
            self.note(specification.identity + ':nqptp', f'AirPlay instance {specification.slot} lost its nqptp region')
            self.halt(target, now)
        if target['unadvertised'] and target['process'] is not None and mapper.idle and self.dbus_ready(now):
            self.halt(target, now)
        if target['process'] is not None:
            mapper.handle(AdapterEvent('level', status))
        elif now >= target['next_start']:
            self.reap(now)
            # Avahi refuses a name that another running instance still advertises, so this one waits for it.
            holder = self.advertised(exclude={specification.identity}).get(target['specification'].name.lower())
            if holder is not None:
                self.note(specification.identity + ':name', f'AirPlay destination {specification.identity[:12]} waits, '
                          f'because instance {holder} still advertises its name')
            elif not any(entry['slot'] == specification.slot for entry in self.stopping):
                # A stopping instance still holds the ports of its slot, so a new one there waits for it to exit.
                self.notes.pop(specification.identity + ':name', None)
                self.start_instance(target, now)

    def advertised(self, exclude=()):
        # Avahi compares names ignoring case, and an instance keeps its name until it has exited.
        names = {entry['name']: entry['slot'] for entry in self.stopping}
        names.update({target['specification'].name.lower(): target['specification'].slot
                      for identity, target in self.targets.items() if target['process'] is not None and identity not in exclude})
        return names

    def rename(self, wanted, now):
        # AirPlay names are only advertised again on restart, so a busy instance keeps its old name until idle.
        renaming = {}
        for identity, specification in wanted.items():
            target = self.targets.get(identity)
            if target is None or specification.name == target['specification'].name:
                continue
            process = target['process']
            # An instance that exited is left to supervise, which records the exit first.
            if process is None or (process.poll() is None and target['mapper'].idle):
                renaming[identity] = specification
        while True:
            # An idle instance whose new name another running instance still holds keeps its old name for now.
            held = self.advertised(exclude=renaming)
            blocked = [identity for identity, specification in renaming.items()
                       if self.targets[identity]['process'] is not None and specification.name.lower() in held]
            if not blocked:
                break
            for identity in blocked:
                holder = held[renaming.pop(identity).name.lower()]
                self.note(identity + ':rename', f'AirPlay instance {self.targets[identity]["specification"].slot} keeps its '
                          f'old name, because instance {holder} still advertises the new one')
        # Every idle instance that changes its name stops first, so two idle rooms can swap names in one pass.
        for identity, specification in renaming.items():
            target = self.targets[identity]
            try:
                if target['process'] is not None:
                    self.halt(target, now)
                target['specification'] = target['mapper'].specification = specification
            except (OSError, ValueError, KeyError, RuntimeError) as error:
                self.failed(identity, error)
            else:
                self.notes.pop(identity + ':rename', None)

    def reconcile(self):
        self.reap(self.clock())
        if not self.configuration:
            return
        now = self.clock()
        wanted = self.wanted()
        result = self.snapcast.request('Server.GetStatus')
        status = {stream['id']: stream.get('status') for stream in result['server']['streams']}
        streams = set(status)
        for identity in [identity for identity in self.targets if identity not in wanted]:
            try:
                self.remove_target(identity, streams)
            except (OSError, ValueError, KeyError, RuntimeError) as error:
                self.failed(identity, error)
        wanted_streams = {specification.stream for specification in wanted.values()}
        for stream in sorted(streams):
            # Only our own streams are touched, never spotify, pc, syren-priority or default.
            if stream.startswith('airplay-') and stream not in wanted_streams:
                try:
                    self.snapcast.request('Stream.RemoveStream', {'id': stream})
                    streams.discard(stream)
                except (OSError, ValueError, RuntimeError) as error:
                    self.failed(stream, error)
        if not self.cleaned:
            # Wanted audio FIFOs stay, so the same inode stays paired with the existing Snapserver stream.
            for fifo in self.runtime.glob('airplay-*.fifo'):
                if fifo.name.removesuffix('.fifo') not in wanted_streams:
                    fifo.unlink(missing_ok=True)
            self.cleaned = True
        self.rename(wanted, now)
        for identity, specification in wanted.items():
            try:
                target = self.targets.get(identity) or self.add_target(specification)
                self.snapcast.ensure_stream(specification, specification.fifo, streams, sample_format=SAMPLE_FORMAT)
                self.supervise(target, specification, status.get(specification.stream), now)
            except (OSError, ValueError, KeyError, RuntimeError) as error:
                self.failed(identity, error)
            else:
                self.notes.pop(identity + ':error', None)
        self.journal.forget(set(wanted) | set(self.targets))

    def watch(self, descriptor, specification, token, kind):
        with self.watch_lock:
            self.watch_count += 1
            identifier = self.watch_count
            self.watches[identifier] = (descriptor, (specification.identity, token, kind, specification.slot))
        self.wake()
        return identifier

    def unwatch(self, identifier):
        with self.watch_lock:
            self.closing.append(identifier)
        self.wake()

    def wake(self):
        try:
            os.write(self.wake_writer, b'.')
        except BlockingIOError:
            pass

    def update_watches(self, selector, registered):
        # Only this thread closes watched descriptors, so a number is never closed twice.
        with self.watch_lock:
            closing, self.closing = self.closing, []
            for identifier in closing:
                entry = self.watches.pop(identifier, None)
                if entry is None:
                    continue
                if registered.pop(identifier, None) is not None:
                    selector.unregister(entry[0])
                os.close(entry[0])
            for identifier, (descriptor, details) in self.watches.items():
                if identifier not in registered:
                    selector.register(descriptor, selectors.EVENT_READ, (identifier, details))
                    registered[identifier] = descriptor

    def read_streams(self):
        # Writers block when nobody reads, so this thread drains every metadata FIFO and log pipe.
        selector = selectors.DefaultSelector()
        selector.register(self.wake_reader, selectors.EVENT_READ, None)
        registered = {}
        try:
            while not self.reader_stopped.is_set():
                self.update_watches(selector, registered)
                for key, events in selector.select(0.2):
                    if key.data is None:
                        while True:
                            try:
                                if not os.read(self.wake_reader, 4096):
                                    break
                            except BlockingIOError:
                                break
                        continue
                    identifier, details = key.data
                    try:
                        data = os.read(key.fd, 65536)
                    except BlockingIOError:
                        continue
                    except OSError:
                        data = b''
                    self.output.put((*details, data))
                    if not data:
                        with self.watch_lock:
                            self.watches.pop(identifier, None)
                            del registered[identifier]
                            selector.unregister(key.fd)
                            os.close(key.fd)
        finally:
            selector.close()

    def received(self, identity, token, kind, slot, data):
        target = self.targets.get(identity)
        current = target is not None and token == target['token']
        if kind == 'stderr':
            self.instance_log(identity, token, slot, data)
        elif current:
            for item_type, code, payload in target['parser'].feed(data):
                if self.trace:
                    log(self.trace.format(slot, item_type, code, payload))
                if item_type == 'ssnc' and code == AVAHI_LOST:
                    self.lost_avahi(target, slot)
                for event in target['adapter'].feed(item_type, code, payload):
                    target['mapper'].handle(event)

    def lost_avahi(self, target, slot):
        if target['unadvertised']:
            return
        # The process still runs, but no phone can find it until it restarts.
        target['unadvertised'] = True
        self.dbus_ok = False
        self.dbus_next_check = self.clock()
        log(f'AirPlay instance {slot} lost Avahi; it restarts when idle')

    def instance_log(self, identity, token, slot, data):
        # A sender can make shairport-sync echo its own text, even a newline, so log lines are never read as signals.
        key = (identity, token)
        target = self.targets.get(identity)
        current = target is not None and token == target['token']
        lines = (self.stderr_buffers.pop(key, b'') + data).split(b'\n')
        rest = lines.pop()
        # Only the running instance keeps a half line, because a halted one never sends the EOF that ends it.
        if not data or not current or len(rest) > STDERR_LIMIT:
            lines.append(rest)
        elif rest:
            self.stderr_buffers[key] = rest
        for line in lines:
            text = line.decode('utf-8', 'replace').rstrip()
            if text:
                log(f'airplay[{slot}] {text}')

    def publish_pending(self):
        if not self.configuration or not self.connected():
            return
        now = time.monotonic()
        sent = {}
        for event in self.journal.events(self.configuration['generation']):
            key = (event['sessionId'], event['eventSequence'])
            previous = self.sent.get(key)
            if previous is None or now - previous >= REPUBLISH_SECONDS:
                self.publish('Lifecycle', event)
                previous = now
            sent[key] = previous
        self.sent = sent

    def prepare_heartbeats(self):
        # The heartbeat thread only reads this tuple, so it never touches the journal while the loop changes it.
        heartbeats = []
        if self.configuration:
            for target in self.targets.values():
                if target['process'] is not None:
                    event = self.journal.heartbeat(target['specification'], self.configuration['generation'])
                    if event:
                        heartbeats.append((target['process'], event))
        self.heartbeats = tuple(heartbeats)

    def publish_heartbeats(self):
        if not self.connected():
            return
        for process, event in self.heartbeats:
            if process.poll() is None:
                self.publish('Lifecycle', event, 0)

    def send_heartbeats(self):
        while not self.stopped.wait(HEARTBEAT_SECONDS):
            try:
                self.publish_heartbeats()
            except (OSError, ValueError, RuntimeError) as error:
                log('Casting heartbeat failed: ' + type(error).__name__)

    def message(self, kind, message):
        if kind == 'connected':
            self.sent.clear()
            # The server offers AirPlay only while this is retained, and the will withdraws it if the supervisor dies.
            self.publish('CastingStatus', {'available': True}, 1, True)
        elif kind == 'Configuration':
            previous = self.configuration
            if message.get('protocolVersion') != 3:
                return
            if previous and message['stateId'] != previous['stateId']:
                # A new household state never continues the old one's sessions.
                self.journal.reset()
                self.catalogue = None
                # A new state counts its generations from the start again.
                self.reconciled_generation = 0
            elif previous and (message['generation'], message['revision']) <= (previous['generation'], previous['revision']):
                return
            if not previous or message['generation'] != previous['generation']:
                self.sent.clear()
            self.configuration = message
            self.next_reconcile = 0
        elif kind == 'Catalogue' and self.configuration and message.get('generation') == self.configuration['generation']:
            if self.catalogue and message['generation'] == self.catalogue['generation'] and message['revision'] <= self.catalogue['revision']:
                return
            self.catalogue = message
            ended = {session['id'] for session in message['sessions'] if session['state'] == 'ended'}
            dropped = [target for target in self.targets.values()
                       if target['mapper'].connected and target['mapper'].session in ended]
            self.journal.acknowledge(message)
            for target in dropped:
                log(f'AirPlay instance {target["specification"].slot} restarts, because the server ended its session')
                self.halt(target, self.clock())
            if message['generation'] > self.reconciled_generation:
                # Only sessions that are really there may clear the interruption of a new generation.
                self.reconciled_generation = message['generation']
                listed = {session['id'] for session in message['sessions'] if session['state'] != 'ended'}
                for target in self.targets.values():
                    if target['mapper'].session in listed:
                        target['mapper'].reconcile()
        elif kind == 'LifecycleRejected' and self.configuration and message.get('generation') == self.configuration['generation']:
            producer, session = message.get('producerId'), message.get('sessionId')
            active = session is not None and self.journal.state['receivers'].get(producer, {}).get('sessionId') == session
            target = self.targets.get(producer)
            if self.journal.abandon(session, producer) and active and target is not None:
                log(f'AirPlay instance {target["specification"].slot} restarts, because the server rejected its session')
                self.halt(target, self.clock())

    def step(self):
        # Each part fails on its own, so a failing reconcile never stops lifecycle publishing.
        try:
            while not self.incoming.empty():
                self.message(*self.incoming.get())
            while not self.output.empty():
                self.received(*self.output.get())
            for target in list(self.targets.values()):
                target['mapper'].tick()
        except (OSError, ValueError, KeyError, RuntimeError) as error:
            log('Casting supervisor message failed: ' + type(error).__name__)
        try:
            self.publish_pending()
            self.prepare_heartbeats()
        except (OSError, ValueError, KeyError, RuntimeError) as error:
            log('Casting lifecycle publishing failed: ' + type(error).__name__)
        if self.clock() >= self.next_reconcile:
            try:
                self.reconcile()
            except (OSError, ValueError, KeyError, RuntimeError) as error:
                log('Casting reconcile failed: ' + type(error).__name__)
                self.reconcile_delay = min(self.reconcile_delay * 2, RECONCILE_RETRY_LIMIT)
            else:
                self.reconcile_delay = RECONCILE_SECONDS
            self.next_reconcile = self.clock() + self.reconcile_delay

    def shutdown(self):
        # The end events go out before the instances stop, so a slow instance cannot hold them past the 4 s limit.
        for target in self.targets.values():
            try:
                target['mapper'].stop()
            except (OSError, ValueError, KeyError) as error:
                log('Casting stop failed: ' + type(error).__name__)
        self.publish_final()
        stop(*[target['process'] for target in self.targets.values()], *[entry['process'] for entry in self.stopping],
             timeout=STOP_SECONDS)
        for target in self.targets.values():
            target['process'] = None
        self.stopping.clear()

    def publish_final(self):
        # Events that do not get out stay in the journal and are ended again on the next start.
        if not self.connected():
            return
        events = self.journal.events(self.configuration['generation']) if self.configuration else []
        deliveries = [self.publish('Lifecycle', event) for event in events]
        status = self.publish('CastingStatus', {'available': False}, 1, True)
        deadline = time.monotonic() + PUBLISH_WAIT_SECONDS
        for delivery in deliveries + [status]:
            if hasattr(delivery, 'wait_for_publish'):
                try:
                    delivery.wait_for_publish(max(0, deadline - time.monotonic()))
                except (RuntimeError, ValueError):
                    pass
        self.withdrawn = hasattr(status, 'is_published') and status.is_published()

    def close(self):
        if self.wake_reader is None:
            return
        with self.watch_lock:
            for descriptor, details in self.watches.values():
                os.close(descriptor)
            self.watches.clear()
            self.closing.clear()
        for target in self.targets.values():
            os.close(target['audio'])
        self.targets.clear()
        os.close(self.wake_reader)
        os.close(self.wake_writer)
        self.wake_reader = self.wake_writer = None
        self.snapcast.close()

    def run(self):
        # Heartbeats and pipe reading run on their own threads, so a slow Snapserver call cannot delay them.
        heartbeat = threading.Thread(target=self.send_heartbeats, daemon=True)
        reader = threading.Thread(target=self.read_streams, daemon=True)
        heartbeat.start()
        reader.start()
        try:
            while not self.stopped.wait(0.02):
                self.step()
        finally:
            self.stopped.set()
            self.heartbeats = ()
            heartbeat.join(2)
            try:
                self.shutdown()
            finally:
                self.reader_stopped.set()
                reader.join(1)
                self.close()


def main():
    import paho.mqtt.client as mqtt

    os.umask(0o077)
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id='syren-casting-supervisor')
    client.reconnect_delay_set(min_delay=1, max_delay=2)
    client.max_queued_messages_set(1000)
    client.will_set(PREFIX + 'CastingStatus', json.dumps({'available': False}), qos=1, retain=True)
    supervisor = CastingSupervisor(os.environ.get('SYREN_CASTING_STATE', '/var/lib/snapserver/casting'),
                                   '/tmp/syren-casting',
                                   lambda topic, message, qos=1, retain=False: client.publish(
                                       PREFIX + topic, json.dumps(message), qos=qos, retain=retain),
                                   connected=client.is_connected, trace=os.environ.get('SYREN_CASTING_TRACE') == '1')

    def connected(client, userdata, flags, reason_code, properties):
        if reason_code == 0:
            client.subscribe([(PREFIX + kind, 1) for kind in ('Configuration', 'Catalogue', 'LifecycleRejected')])
            supervisor.incoming.put(('connected', {}))

    def message(client, userdata, message):
        try:
            supervisor.incoming.put((message.topic.removeprefix(PREFIX), json.loads(message.payload)))
        except (ValueError, UnicodeDecodeError):
            pass

    client.on_connect, client.on_message = connected, message
    client.connect_async(os.environ.get('MQTT_HOST', '127.0.0.1'), int(os.environ.get('MQTT_PORT', '1883')), 5)
    client.loop_start()
    for signal_number in (signal.SIGINT, signal.SIGTERM):
        signal.signal(signal_number, lambda *_: supervisor.stopped.set())
    try:
        supervisor.run()
    finally:
        # A clean disconnect drops the will, so it happens only once the broker has the withdrawn status.
        if supervisor.withdrawn:
            client.disconnect()
            client.loop_stop()


if __name__ == '__main__':
    main()
