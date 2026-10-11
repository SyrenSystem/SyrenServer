#!/usr/bin/env python3
"""Run two confined AirPlay 2 instances against nqptp and a private bus behind the Avahi relay."""

import argparse
import json
import os
from pathlib import Path
import plistlib
import selectors
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time

# python3 -I leaves the script directory off the path, and the modules under test sit next to this file.
sys.path.insert(0, str(Path(__file__).resolve().parent))

from casting_dbus_relay import check_denied
from casting_sessions import CastingSpec, MetadataParser, instance_command, render_config
from spotify_sessions import receiver_identity

BUS_SOCKET = '/tmp/smoke-bus'
RELAY_SOCKET = '/tmp/smoke-relay'
BUS_CONFIGURATION = f'''<!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-BUS Bus Configuration 1.0//EN"
 "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
<busconfig>
  <type>system</type>
  <listen>unix:path={BUS_SOCKET}</listen>
  <auth>EXTERNAL</auth>
  <policy context="default">
    <allow user="*"/>
    <allow own="*"/>
    <allow send_destination="*"/>
    <allow receive_sender="*"/>
  </policy>
</busconfig>
'''


def wait_for(check, seconds, description):
    deadline = time.monotonic() + seconds
    while True:
        result = check()
        if result:
            return result
        if time.monotonic() >= deadline:
            raise AssertionError(f'Waited {seconds} s for {description}')
        time.sleep(.05)


def udp_port_bound(port):
    # The control port is UDP, so it shows in the kernel socket tables instead of answering a connect.
    for table in ('/proc/net/udp', '/proc/net/udp6'):
        try:
            lines = Path(table).read_text().splitlines()[1:]
        except OSError:
            continue
        if any(line.split()[1].rsplit(':', 1)[1] == f'{port:04X}' for line in lines):
            return True
    return False


def rtsp(port, request):
    with socket.create_connection(('127.0.0.1', port), timeout=3) as connection:
        connection.sendall(request)
        data = b''
        while b'\r\n\r\n' not in data:
            chunk = connection.recv(65536)
            if not chunk:
                break
            data += chunk
        head, _, body = data.partition(b'\r\n\r\n')
        length = next((int(line.split(b':', 1)[1]) for line in head.split(b'\r\n')
                       if line.lower().startswith(b'content-length:')), 0)
        while len(body) < length:
            chunk = connection.recv(65536)
            if not chunk:
                break
            body += chunk
        return head.split(b'\r\n')[0].decode(), body


def realtime_allowed(priority):
    # A build without CAP_SYS_NICE or a real time limit cannot set FIFO, so a short lived chrt finds out first.
    return subprocess.run(['chrt', '--fifo', str(priority), 'true'], capture_output=True).returncode == 0


def realtime_priority(command):
    # The build runs as root, where the production command always adds chrt, so the smoke drops chrt when the build cannot set FIFO.
    return int(command[command.index('chrt') + 2]) if 'chrt' in command else None


def without_realtime(command):
    index = command.index('chrt') if 'chrt' in command else None
    return command if index is None else command[:index] + command[index + 3:]


def landlock_allowed():
    # A builder whose seccomp profile blocks the Landlock calls cannot confine, so a short lived setpriv finds out first.
    return subprocess.run(['/usr/bin/setpriv', '--landlock-access', 'fs', '--landlock-rule',
                           'path-beneath:execute,read-file,read-dir:/usr', '--', '/usr/bin/true'],
                          capture_output=True).returncode == 0


def without_landlock(command):
    # Only the Landlock options go, so the smoke still runs setpriv with pdeathsig and the rest of the command.
    end = command.index('--')
    options, index = [], 0
    while index < end:
        if command[index] in ('--landlock-access', '--landlock-rule'):
            index += 2
        else:
            options.append(command[index])
            index += 1
    return options + command[end:]


def thread_scheduling(process_id):
    result = set()
    for task in os.listdir(f'/proc/{process_id}/task'):
        try:
            result.add((os.sched_getscheduler(int(task)), os.sched_getparam(int(task)).sched_priority))
        except OSError:
            pass
    return result


class Instance:
    def __init__(self, specification, environment, executable, realtime, confined):
        self.specification = specification
        self.command = instance_command(specification, executable)
        if not realtime:
            self.command = without_realtime(self.command)
        if not confined:
            self.command = without_landlock(self.command)
        self.lines = []
        self.process = subprocess.Popen(self.command, env=environment, stdout=subprocess.DEVNULL,
                                        stderr=subprocess.PIPE)
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()

    def read(self):
        for line in self.process.stderr:
            self.lines.append(line.decode('utf-8', 'replace').rstrip())

    def logged(self, text):
        return any(text in line for line in self.lines)

    def log(self):
        return '\n'.join(f'airplay[{self.specification.slot}] {line}' for line in self.lines)


class MetadataReader:
    """Drain every metadata FIFO, as the supervisor does, so shairport-sync never blocks on a write."""

    def __init__(self, specifications):
        self.items = {specification.slot: [] for specification in specifications}
        self.selector = selectors.DefaultSelector()
        self.descriptors = []
        for specification in specifications:
            descriptor = os.open(specification.meta, os.O_RDWR | os.O_NONBLOCK)
            self.descriptors.append(descriptor)
            self.selector.register(descriptor, selectors.EVENT_READ, (specification.slot, MetadataParser()))
        self.stopped = threading.Event()
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def run(self):
        while not self.stopped.is_set():
            for key, _ in self.selector.select(.1):
                slot, parser = key.data
                try:
                    data = os.read(key.fd, 65536)
                except BlockingIOError:
                    continue
                self.items[slot] += parser.feed(data)

    def names(self, slot):
        return [data.decode('utf-8', 'replace') for kind, code, data in self.items[slot]
                if kind == 'ssnc' and code == 'svna' and data]

    def codes(self, slot):
        return [code for kind, code, data in self.items[slot] if kind == 'ssnc']

    def close(self):
        self.stopped.set()
        self.thread.join(timeout=2)
        for descriptor in self.descriptors:
            os.close(descriptor)


def denials(path):
    return sum(1 for line in Path(path).read_text().splitlines() if line.startswith('Denied'))


def start_relay(relay_log):
    with relay_log.open('a') as log:
        relay = subprocess.Popen([sys.executable, '-I', str(Path(__file__).resolve().parent / 'casting_dbus_relay.py'),
                                  '--listen', RELAY_SOCKET, '--upstream', BUS_SOCKET,
                                  '--allow-uid', str(os.getuid())], stdout=log, stderr=log)
    wait_for(lambda: os.path.exists(RELAY_SOCKET), 5, 'the relay socket')
    return relay


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    # Landlock only lets the instances run files under /usr, as in the runtime image.
    parser.add_argument('--shairport-sync', default='/usr/local/bin/shairport-sync')
    parser.add_argument('--nqptp', default='/usr/local/bin/nqptp')
    arguments = parser.parse_args()
    specifications = [CastingSpec(receiver_identity('smoke', 'casting:airplay', destination), destination, name, slot)
                      for slot, (destination, name) in enumerate([('house', 'Smoke A'), ('smoke-room', 'Smoke B')])]
    runtime = specifications[0].runtime
    planned = realtime_priority(instance_command(specifications[0], arguments.shairport_sync))
    realtime = planned is not None and realtime_allowed(planned)
    confined = landlock_allowed()
    environment = dict(os.environ, DBUS_SYSTEM_BUS_ADDRESS=f'unix:path={RELAY_SOCKET}')
    instances = []
    held = []
    bus_process_id = relay = nqptp = metadata = None
    summary = {}
    with tempfile.TemporaryDirectory(prefix='syren-casting-smoke-') as temporary:
        directory = Path(temporary)
        relay_log = directory / 'relay.log'
        nqptp_log = directory / 'nqptp.log'
        try:
            (directory / 'bus.conf').write_text(BUS_CONFIGURATION)
            started = subprocess.run(['dbus-daemon', f'--config-file={directory / "bus.conf"}', '--fork', '--nopidfile',
                                      '--print-pid'], capture_output=True, text=True, timeout=10, check=True)
            bus_process_id = int(started.stdout.split()[0])
            relay = start_relay(relay_log)
            with nqptp_log.open('w') as log:
                nqptp = subprocess.Popen([arguments.nqptp, '-v'], stdout=log, stderr=log)
            wait_for(lambda: udp_port_bound(9000), 5, 'the nqptp control port')

            runtime.mkdir(mode=0o700, parents=True, exist_ok=True)
            # setpriv needs every Landlock path to exist, and production mounts the relay directory here.
            os.makedirs('/run/syren-avahi-relay', exist_ok=True)
            for specification in specifications:
                descriptor = os.open(specification.config, os.O_CREAT | os.O_TRUNC | os.O_WRONLY, 0o600)
                with os.fdopen(descriptor, 'w') as stream:
                    stream.write(render_config(specification, log_verbosity=1))
                for fifo in (specification.fifo, specification.meta):
                    if not fifo.exists():
                        os.mkfifo(fifo, 0o600)
                # Holding the audio FIFO open means a reader always exists, as in the supervisor.
                held.append(os.open(specification.fifo, os.O_RDWR | os.O_NONBLOCK))
            metadata = MetadataReader(specifications)
            instances = [Instance(specification, environment, arguments.shairport_sync, realtime, confined)
                         for specification in specifications]

            time.sleep(3)
            for instance in instances:
                assert instance.process.poll() is None, f'Instance {instance.specification.slot} exited'
                assert instance.logged('Startup in AirPlay 2 mode'), f'Instance {instance.specification.slot} is not AirPlay 2'
            regions = [specification.shm_name for specification in specifications]
            for region in regions:
                assert os.path.exists('/dev/shm' + region), f'nqptp made no {region}'
            summary['shared_memory'] = list(regions)
            if confined:
                summary['landlock'] = 'file rules applied'
            else:
                summary['landlock_skipped'] = 'this builder cannot use Landlock, so the instances ran without the file rules'

            priority = realtime_priority(instances[0].command)
            if priority is not None:
                # The patch keeps every thread at the inherited priority instead of FIFO 2 or 3.
                for instance in instances:
                    scheduling = thread_scheduling(instance.process.pid)
                    assert scheduling == {(os.SCHED_FIFO, priority)}, f'Threads run at {scheduling}'
            summary['priority'] = priority
            if planned is not None and not realtime:
                summary['priority_skipped'] = f'this build cannot set FIFO {planned}, so the thread check did not run'

            summary['instances'] = []
            for instance in instances:
                specification = instance.specification
                status, _ = rtsp(specification.port, b'OPTIONS * RTSP/1.0\r\nCSeq: 1\r\n\r\n')
                assert status.split()[1:2] == ['200'], f'OPTIONS on {specification.port} answered {status}'
                status, body = rtsp(specification.port, b'GET /info RTSP/1.0\r\nCSeq: 2\r\n\r\n')
                assert status.split()[1:2] == ['200'], f'GET /info on {specification.port} answered {status}'
                information = plistlib.loads(body)
                device_id = str(information.get('deviceID', '')).replace(':', '').upper()
                assert device_id == specification.device_id, f'/info device ID {device_id} for {specification.device_id}'
                assert information.get('name') == specification.name, f'/info name {information.get("name")}'
                wait_for(lambda: specification.name in metadata.names(specification.slot), 5, f'svna of {specification.name}')
                summary['instances'].append({'port': specification.port, 'device_id': device_id,
                                             'name': specification.name, 'svna': True})

            # Stray names must neither stop nqptp nor use up its 16 records, and a real slot name still works after them.
            spare = '/syren-airplay-2'
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as control:
                for name in ['/a/b'] + [f'/junk-{index}' for index in range(16)] + [spare]:
                    control.sendto(name.encode() + b' T\0', ('127.0.0.1', 9000))
            wait_for(lambda: os.path.exists('/dev/shm' + spare), 2, f'nqptp to make {spare} after stray names')
            assert nqptp.poll() is None, 'nqptp exited after a stray control message'
            for region in regions:
                assert os.path.exists('/dev/shm' + region), f'nqptp lost {region} after stray names'
            regions.append(spare)
            summary['nqptp_stray_names'] = 'ignored'

            assert denials(relay_log) == 0, relay_log.read_text()
            assert check_denied(RELAY_SOCKET), 'The relay passed RequestName'
            wait_for(lambda: denials(relay_log) == 1, 2, 'the relay to log the denied RequestName')
            summary['relay'] = 'passes avahi-client and denies RequestName'

            # The supervisor restarts an instance only on the patch's syav item, so losing the bus must send it.
            relay.terminate()
            relay.wait(timeout=5)
            for instance in instances:
                slot = instance.specification.slot
                wait_for(lambda: 'syav' in metadata.codes(slot), 5, f'the syav item from instance {slot}')
                assert instance.process.poll() is None, f'Instance {slot} exited after losing the bus'
            summary['avahi_lost'] = 'syav on both metadata pipes'
            relay = start_relay(relay_log)

            for instance in instances:
                instance.process.send_signal(signal.SIGTERM)
            deadline = time.monotonic() + 2
            for instance in instances:
                try:
                    instance.process.wait(timeout=max(deadline - time.monotonic(), 0))
                except subprocess.TimeoutExpired:
                    raise AssertionError(f'Instance {instance.specification.slot} ignored SIGTERM for 2 s') from None
            summary['sigterm'] = 'both exited within 2 s'

            # SIGTERM makes nqptp unlink its regions, so a new instance cannot find stale timing.
            nqptp.send_signal(signal.SIGTERM)
            nqptp.wait(timeout=5)
            for region in regions:
                assert not os.path.exists('/dev/shm' + region), f'nqptp left {region} behind'
            lonely = Instance(specifications[0], environment, arguments.shairport_sync, realtime, confined)
            instances.append(lonely)
            lonely.process.wait(timeout=10)
            lonely.reader.join(timeout=2)
            assert lonely.process.returncode != 0, 'An instance started without nqptp'
            assert lonely.logged('The NQPTP service can not be found'), lonely.log()
            summary['without_nqptp'] = f'exit {lonely.process.returncode}'
            print(json.dumps(summary))
        except Exception:
            for instance in instances:
                print(instance.log())
            for path in (nqptp_log, relay_log):
                if path.exists():
                    print(path.read_text())
            raise
        finally:
            for instance in instances:
                if instance.process.poll() is None:
                    instance.process.kill()
                    instance.process.wait(timeout=3)
            if metadata is not None:
                metadata.close()
            for descriptor in held:
                os.close(descriptor)
            for process in (nqptp, relay):
                if process is not None and process.poll() is None:
                    process.terminate()
                    process.wait(timeout=3)
            if bus_process_id is not None:
                try:
                    os.kill(bus_process_id, signal.SIGTERM)
                except ProcessLookupError:
                    pass


if __name__ == '__main__':
    main()
