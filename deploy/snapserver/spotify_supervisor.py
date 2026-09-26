#!/usr/bin/python3
"""Keep personal Spotify destinations available without an app connection."""

import hashlib
import json
import os
from pathlib import Path
import queue
import re
import shutil
import signal
import socket
import subprocess
import threading
import time
import urllib.parse
import uuid

from spotify_sessions import LifecycleJournal, ReceiverSpec, atomic_json, destinations, personal_receivers, read_json, receiver_identity

PREFIX = 'SyrenSystem/v3/'
REPUBLISH_SECONDS = 2
HEARTBEAT_SECONDS = .75
RECONCILE_RETRY_SECONDS = 1
RECONCILE_RETRY_LIMIT = 10
SNAPCAST_REPLY_SECONDS = 3
PENDING_LINK_SECONDS = 900
GUEST_ACCOUNTS_PER_DESTINATION = 4


def private_directory(path):
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    if path.is_symlink() or path.stat().st_uid != os.getuid():
        raise ValueError('Invalid credential directory')
    path.chmod(0o700)
    return path


def credentials(path):
    if path.is_symlink() or path.stat().st_uid != os.getuid() or path.stat().st_mode & 0o077:
        raise ValueError('Credentials must be private')
    result = json.loads(path.read_text())
    if not result.get('username') or not result.get('auth_data'):
        raise ValueError('Incomplete credentials')
    return result


def linked_credentials(path):
    # Librespot writes this file in place, so a partial file means the pairing is still being saved.
    if not path.exists():
        return None
    path.chmod(0o600)
    try:
        return credentials(path)
    except ValueError:
        return None


def discovery_address():
    try:
        route = subprocess.run(['ip', '-4', 'route', 'get', '1.1.1.1'], capture_output=True, text=True,
                               timeout=2, check=True).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    match = re.search(r' src (\S+)', route)
    return match.group(1) if match else None


def stop(process):
    if process and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=2)


class SnapControl:
    def __init__(self, host='127.0.0.1', port=1705):
        self.host, self.port = host, port
        self.sequence = 0
        self.connection = self.reader = None

    def close(self):
        if self.reader is not None:
            self.reader.close()
        if self.connection is not None:
            self.connection.close()
        self.reader = self.connection = None

    def request(self, method, parameters=None):
        self.sequence += 1
        deadline = time.monotonic() + SNAPCAST_REPLY_SECONDS
        try:
            if self.connection is None:
                self.connection = socket.create_connection((self.host, self.port), timeout=SNAPCAST_REPLY_SECONDS)
                self.reader = self.connection.makefile('r')
            self.connection.sendall((json.dumps({'jsonrpc': '2.0', 'id': self.sequence, 'method': method,
                                                'params': parameters or {}}) + '\n').encode())
            while True:
                # Notifications do not extend the wait for the reply.
                if time.monotonic() >= deadline:
                    raise TimeoutError('Snapserver did not answer in time')
                line = self.reader.readline(1024 * 1024)
                if not line:
                    raise ConnectionError('Snapserver closed control connection')
                result = json.loads(line)
                if result.get('id') == self.sequence:
                    if 'error' in result:
                        raise RuntimeError('Snapserver rejected stream operation')
                    return result.get('result')
        except (OSError, ValueError, RuntimeError):
            self.close()
            raise

    def streams(self):
        return {stream['id'] for stream in self.request('Server.GetStatus')['server']['streams']}

    def ensure_stream(self, specification, fifo, streams):
        if specification.stream in streams:
            return
        if not fifo.exists():
            os.mkfifo(fifo, 0o600)
        uri = f'pipe://{fifo}?name={specification.stream}&sampleformat=44100:16:2&codec=pcm'
        self.request('Stream.AddStream', {'streamUri': uri})
        streams.add(specification.stream)


class SpotifySupervisor:
    def __init__(self, root, runtime, publish, snapcast=None, connected=lambda: True, discovery=None):
        self.root = private_directory(Path(root))
        self.runtime = private_directory(Path(runtime))
        self.publish = publish
        self.connected = connected
        self.discovery = discovery
        self.snapcast = snapcast or SnapControl()
        self.journal = LifecycleJournal(self.root / 'lifecycle.json')
        self.guests = read_json(self.root / 'guests.json', {})
        self.configuration = None
        self.catalogue = None
        self.processes = {}
        self.links = {}
        self.incoming = queue.SimpleQueue()
        self.stopped = threading.Event()
        self.event_socket = self.runtime / 'events.sock'
        self.event_socket.unlink(missing_ok=True)
        self.socket = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
        self.socket.bind(str(self.event_socket))
        self.socket.setblocking(False)
        self.sent = {}
        self.failures = {}
        self.next_reconcile = 0
        self.reconcile_delay = RECONCILE_RETRY_SECONDS
        self.heartbeats = ()

    def account_directory(self, profile):
        return private_directory(self.root / 'accounts' / hashlib.sha256(profile.encode()).hexdigest())

    def receiver_directory(self, identity):
        return private_directory(self.root / 'receivers' / identity)

    def command(self, specification, cache):
        command = ['/usr/local/bin/librespot', '--name', specification.name, '--device-id', specification.identity,
                   '--system-cache', str(cache), '--disable-audio-cache', '--backend', 'pipe', '--format', 'S16',
                   '--dither', 'none', '--volume-ctrl', 'linear', '--initial-volume', '100', '--bitrate', '320', '--ap-port', '443']
        if specification.guest:
            command.append('--lock-discovery-account')
            if self.discovery:
                command += ['--zeroconf-interface', self.discovery]
        else:
            command.append('--disable-discovery')
        return command

    def wanted(self):
        configuration = self.configuration
        wanted = personal_receivers(configuration)
        valid_destinations = dict(destinations(configuration))
        state_id = configuration['stateId']
        guests = self.retained_guests(valid_destinations)
        for destination in valid_destinations:
            if not any(guest['destination'] == destination and not guest.get('account') for guest in guests):
                slot = uuid.uuid4().hex
                guests.append({'identity': receiver_identity(state_id, 'guest:' + slot, destination),
                               'destination': destination, 'account': None})
        if self.guests != {state_id: guests}:
            self.guests = {state_id: guests}
            atomic_json(self.root / 'guests.json', self.guests)
        for guest in guests:
            wanted.append(ReceiverSpec(guest['identity'], None, guest.get('account'), guest['destination'],
                                      valid_destinations[guest['destination']] + ' · Guest', True))
        return {specification.identity: specification for specification in wanted}

    def retained_guests(self, valid_destinations):
        linked = {profile['spotifyAccountId'] for profile in self.configuration['profiles'] if profile.get('spotifyAccountId')}

        def idle(guest):
            return self.journal.state['receivers'].get(guest['identity'], {}).get('sessionId') is None

        guests = [guest for guest in self.guests.get(self.configuration['stateId'], [])
                  if guest['destination'] in valid_destinations and not (guest.get('account') in linked and idle(guest))]
        retained = []
        for destination in valid_destinations:
            claimed = sorted((guest for guest in guests if guest['destination'] == destination and guest.get('account')),
                             key=lambda guest: guest.get('claimedAt', 0), reverse=True)
            # Each new guest account claims a slot, so the oldest idle ones are released.
            for index, guest in enumerate(claimed):
                if index < GUEST_ACCOUNTS_PER_DESTINATION or not idle(guest):
                    retained.append(guest)
            retained += [guest for guest in guests if guest['destination'] == destination and not guest.get('account')][:1]
        return retained

    def start_receiver(self, specification, streams):
        cache = self.receiver_directory(specification.identity)
        if not specification.guest:
            origin = self.account_directory(specification.profile) / 'credentials.json'
            if not origin.exists() or credentials(origin)['username'] != specification.account:
                return
            if not (cache / 'credentials.json').exists() or credentials(cache / 'credentials.json')['username'] != specification.account:
                atomic_json(cache / 'credentials.json', credentials(origin))
        elif (cache / 'credentials.json').exists():
            credentials(cache / 'credentials.json')
        fifo = self.runtime / (specification.stream + '.fifo')
        self.snapcast.ensure_stream(specification, fifo, streams)
        instance = uuid.uuid4().hex
        name_file = cache / 'name.txt'
        name_file.write_text(specification.name)
        environment = dict(os.environ, RUST_LOG='off', SYREN_EVENT_SOCKET=str(self.event_socket),
                           SYREN_RECEIVER_ID=specification.identity, SYREN_RECEIVER_INSTANCE=instance,
                           SYREN_NAME_FILE=str(name_file))
        command = self.command(specification, cache) + ['--device', str(fifo), '--onevent', '/usr/local/bin/spotify_event.py']
        process = subprocess.Popen(command, env=environment, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.processes[specification.identity] = {'specification': specification, 'process': process, 'instance': instance,
                                                   'playing': False, 'reconciledGeneration': 0}

    def finish_pending_links(self):
        for profile in self.configuration['profiles']:
            directory = self.account_directory(profile['id'])
            pending = directory / 'pending.json'
            if pending.exists():
                request = read_json(pending, {})
                account = request.get('credentials', {})
                if (request.get('stateId') == self.configuration['stateId'] and account.get('auth_data') and
                        profile.get('spotifyAccountId') == account.get('username')):
                    atomic_json(directory / 'credentials.json', account)
                    pending.unlink()
                elif (profile.get('spotifyAccountId') or request.get('stateId') != self.configuration['stateId'] or
                      time.time() - pending.stat().st_mtime >= PENDING_LINK_SECONDS):
                    pending.unlink()
            if not profile.get('spotifyAccountId'):
                (directory / 'credentials.json').unlink(missing_ok=True)

    def remove_unused_credentials(self, wanted):
        profiles = {hashlib.sha256(profile['id'].encode()).hexdigest() for profile in self.configuration['profiles']}
        for kind, keep in (('accounts', profiles), ('receivers', set(wanted) | set(self.processes))):
            directory = self.root / kind
            if directory.is_dir():
                for child in directory.iterdir():
                    if child.name not in keep:
                        shutil.rmtree(child)

    def failed(self, identity, error):
        if self.failures.get(identity) != type(error).__name__:
            self.failures[identity] = type(error).__name__
            print(f'Spotify receiver {identity[:12]} failed: {type(error).__name__}', flush=True)

    def reconcile(self):
        if not self.configuration:
            return
        self.finish_pending_links()
        wanted = self.wanted()
        streams = self.snapcast.streams()
        for identity, running in list(self.processes.items()):
            specification = wanted.get(identity)
            try:
                if specification is None or running['process'].poll() is not None or (not specification.guest and specification.account != running['specification'].account):
                    self.journal.emit(running['specification'], 'interrupt' if specification == running['specification'] else 'end')
                    stop(running['process'])
                    del self.processes[identity]
                    if specification is None and running['specification'].stream in streams:
                        self.snapcast.request('Stream.RemoveStream', {'id': running['specification'].stream})
                        streams.discard(running['specification'].stream)
                elif running['specification'].name != specification.name:
                    if specification.guest and specification.account is None:
                        stop(running['process'])
                        del self.processes[identity]
                    else:
                        (self.receiver_directory(identity) / 'name.txt').write_text(specification.name)
                        running['specification'] = specification
                if identity in self.processes:
                    self.snapcast.ensure_stream(specification, self.runtime / (specification.stream + '.fifo'), streams)
            except (OSError, ValueError, KeyError, RuntimeError) as error:
                self.failed(identity, error)
        for identity, specification in wanted.items():
            if identity not in self.processes:
                try:
                    self.start_receiver(specification, streams)
                except (OSError, ValueError, KeyError, RuntimeError) as error:
                    self.failed(identity, error)
                else:
                    self.failures.pop(identity, None)
        self.remove_unused_credentials(wanted)
        self.journal.forget(set(wanted) | set(self.processes))

    def event(self, event):
        running = self.processes.get(event.get('receiverId'))
        if not running or event.get('instanceId') != running['instance']:
            return
        specification = running['specification']
        account = event.get('accountId')
        if specification.guest and account:
            for guest in self.guests[self.configuration['stateId']]:
                if guest['identity'] == specification.identity:
                    if guest.get('account') and guest['account'] != account:
                        return
                    if not guest.get('account'):
                        guest.update(account=account, claimedAt=time.time())
                        atomic_json(self.root / 'guests.json', self.guests)
        elif account and account != specification.account:
            return
        if event['event'] == 'playing':
            running['playing'] = True
        elif event['event'] in ('loading', 'stopped', 'unavailable') or event.get('action') in ('pause', 'end', 'interrupt'):
            running['playing'] = False
        self.journal.handle(specification, event)

    def begin_link(self, request):
        configuration = self.configuration
        if not configuration or request.get('generation') != configuration['generation'] or request.get('stateId') != configuration['stateId']:
            return
        ticket = request.get('ticket', '')
        profile = next((profile for profile in configuration['profiles'] if profile['id'] == request.get('profileId')), None)
        if not re.fullmatch('[a-f0-9]{32}', ticket) or not profile or profile.get('spotifyAccountId') or ticket in self.links:
            return
        self.links[ticket] = {'request': request, 'acknowledged': threading.Event(), 'status': None}
        threading.Thread(target=self.link, args=(request,), daemon=True).start()

    def link(self, request):
        ticket = request['ticket']
        cache = private_directory(self.root / 'linking' / ticket)
        specification = ReceiverSpec(receiver_identity(request['stateId'], ticket, 'link'), request['profileId'], None,
                                     'house', 'SyrenSystem linking')
        process = subprocess.Popen(self.command(specification, cache) + ['--enable-device-auth', '--device', '/dev/null'],
                                   env=dict(os.environ, RUST_LOG='off'), stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        timer = threading.Timer(600, lambda: stop(process))
        timer.start()
        try:
            for line in iter(process.stdout.readline, b''):
                match = re.search(rb'https://spotify\.com/pair\?code=[A-Z0-9]+', line)
                if match:
                    self.publish('SpotifyLinkStatus', {'ticket': ticket, 'status': 'pair', 'url': match.group().decode()})
                    break
            while process.poll() is None and not self.stopped.wait(0.2):
                account = linked_credentials(cache / 'credentials.json')
                if account:
                    self.finish_link(request, account)
                    return
            self.publish('SpotifyLinkStatus', {'ticket': ticket, 'status': 'expired'})
        except (OSError, ValueError):
            self.publish('SpotifyLinkStatus', {'ticket': ticket, 'status': 'failed'})
        finally:
            timer.cancel()
            stop(process)
            if process.stdout:
                process.stdout.close()
            shutil.rmtree(cache)

    def finish_link(self, request, account):
        ticket = request['ticket']
        directory = self.account_directory(request['profileId'])
        atomic_json(directory / 'pending.json', {'stateId': request['stateId'], 'credentials': account})
        self.publish('SpotifyLinked', dict(request, accountId=account['username']))
        pending = self.links[ticket]
        # A linked account is moved into place by reconcile once the configuration shows it, even after a lost confirmation.
        if not pending['acknowledged'].wait(10):
            self.publish('SpotifyLinkStatus', {'ticket': ticket, 'status': 'failed'})
        elif pending['status'] != 'linked':
            (directory / 'pending.json').unlink(missing_ok=True)

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
            for running in self.processes.values():
                event = self.journal.heartbeat(running['specification'], self.configuration['generation'])
                if event:
                    heartbeats.append((running['process'], event))
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
                print('Spotify heartbeat failed: ' + type(error).__name__, flush=True)

    def message(self, kind, message):
        if kind == 'connected':
            self.sent.clear()
        elif kind == 'Configuration':
            previous = self.configuration
            if message.get('protocolVersion') != 3:
                return
            if previous and message['stateId'] != previous['stateId']:
                # A new household state never continues the old one's sessions.
                self.journal.reset()
                self.catalogue = None
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
            for running in self.processes.values():
                if self.journal.receiver(running['specification'].identity)['sessionId'] in ended:
                    stop(running['process'])
            self.journal.acknowledge(message)
            for running in self.processes.values():
                specification = running['specification']
                receiver = self.journal.receiver(specification.identity)
                session = next((session for session in message['sessions'] if session['id'] == receiver['sessionId']), None)
                if session and running['playing'] and running['reconciledGeneration'] < message['generation']:
                    running['reconciledGeneration'] = message['generation']
                    self.journal.emit(specification, 'reconcile')
        elif kind == 'LifecycleRejected' and self.configuration and message.get('generation') == self.configuration['generation']:
            self.journal.abandon(message.get('sessionId'), message.get('producerId'))
        elif kind == 'SpotifyLinkRequest':
            self.begin_link(message)
        elif kind == 'SpotifyLinkStatus' and message.get('status') in ('linked', 'rejected'):
            pending = self.links.get(message.get('ticket'))
            if pending:
                pending['status'] = message['status']
                pending['acknowledged'].set()

    def step(self):
        # Each part fails on its own, so a failing reconcile never stops lifecycle publishing.
        try:
            while not self.incoming.empty():
                self.message(*self.incoming.get())
            while True:
                try:
                    self.event(json.loads(self.socket.recv(8192)))
                except BlockingIOError:
                    break
        except (OSError, ValueError, KeyError, RuntimeError) as error:
            print('Spotify supervisor message failed: ' + type(error).__name__, flush=True)
        try:
            self.publish_pending()
            self.prepare_heartbeats()
        except (OSError, ValueError, KeyError, RuntimeError) as error:
            print('Spotify lifecycle publishing failed: ' + type(error).__name__, flush=True)
        if time.monotonic() >= self.next_reconcile:
            try:
                self.reconcile()
            except (OSError, ValueError, KeyError, RuntimeError) as error:
                print('Spotify reconcile failed: ' + type(error).__name__, flush=True)
                self.reconcile_delay = min(self.reconcile_delay * 2, RECONCILE_RETRY_LIMIT)
            else:
                self.reconcile_delay = RECONCILE_RETRY_SECONDS
            self.next_reconcile = time.monotonic() + self.reconcile_delay

    def run(self):
        # Heartbeats run on their own thread, so a slow Snapserver call cannot delay them.
        heartbeat = threading.Thread(target=self.send_heartbeats, daemon=True)
        heartbeat.start()
        try:
            while not self.stopped.wait(0.02):
                self.step()
        finally:
            self.stopped.set()
            self.heartbeats = ()
            heartbeat.join(2)
            for running in self.processes.values():
                self.journal.emit(running['specification'], 'interrupt')
                stop(running['process'])
            self.snapcast.close()
            self.socket.close()
            self.event_socket.unlink(missing_ok=True)


def main():
    import paho.mqtt.client as mqtt

    os.umask(0o077)
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id='syren-spotify-supervisor')
    client.reconnect_delay_set(min_delay=1, max_delay=2)
    client.max_queued_messages_set(1000)
    discovery = discovery_address()
    if discovery is None:
        print('Could not detect the IPv4 address for guest Spotify discovery', flush=True)
    supervisor = SpotifySupervisor(os.environ.get('SYREN_SPOTIFY_STATE', '/var/lib/snapserver/spotify'),
                                   '/tmp/syren-spotify',
                                   lambda topic, message, qos=1: client.publish(PREFIX + topic, json.dumps(message), qos=qos),
                                   connected=client.is_connected, discovery=discovery)

    def connected(client, userdata, flags, reason_code, properties):
        if reason_code == 0:
            client.subscribe([(PREFIX + kind, 1) for kind in ('Configuration', 'Catalogue', 'LifecycleRejected',
                                                               'SpotifyLinkRequest', 'SpotifyLinkStatus')])
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
        client.disconnect()
        client.loop_stop()


if __name__ == '__main__':
    main()
