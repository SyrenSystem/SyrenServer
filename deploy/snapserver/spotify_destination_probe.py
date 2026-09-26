#!/usr/bin/env python3
"""Check personal Spotify destinations before enabling the session playback model."""

import argparse
import array
import hashlib
import json
import math
import os
from pathlib import Path
import re
import selectors
import signal
import stat
import subprocess
import sys
import time
import uuid


DEFAULT_STATE = Path.home() / '.local/state/syrensystem/spotify-destination-probe'
DEFAULT_IMAGE = 'localhost/syren-snapserver-check:latest'


def identifier(value):
    if not re.fullmatch(r'[a-z0-9][a-z0-9_-]{0,63}', value):
        raise ValueError('Identifiers require 1 to 64 lowercase letters, digits, underscores or hyphens')
    return value


def private_directory(directory):
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    metadata = directory.lstat()
    if not stat.S_ISDIR(metadata.st_mode) or metadata.st_uid != os.getuid():
        raise ValueError('The private state directory must be an owned directory')
    directory.chmod(0o700)
    return directory


def read_credentials(directory):
    descriptor = os.open(directory / 'credentials.json', os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(descriptor) as stream:
        metadata = os.fstat(stream.fileno())
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_uid != os.getuid() or metadata.st_mode & 0o077:
            raise ValueError('Credentials must be an owned private regular file')
        credentials = json.load(stream)
    if not credentials.get('username') or not credentials.get('auth_data'):
        raise ValueError('The Spotify account has not finished linking')
    return credentials


def device_id(namespace, profile, destination):
    identity = json.dumps([namespace, identifier(profile), identifier(destination)], separators=(',', ':'))
    return hashlib.sha1(identity.encode()).hexdigest()


def namespace_for(state):
    path = state / 'namespace'
    try:
        with path.open('x') as stream:
            stream.write(str(uuid.uuid4()))
    except FileExistsError:
        pass
    return str(uuid.UUID(path.read_text()))


def command_for(arguments, cache, receiver_id, name, container_name, linking=False):
    command = [
        'podman', 'run', '--rm', '--name', container_name, '--network', 'host', '--log-driver', 'none',
        '--userns', 'keep-id', '--user', f'{os.getuid()}:{os.getgid()}',
        '--read-only', '--cap-drop', 'all', '--security-opt', 'no-new-privileges',
        '--tmpfs', '/tmp:rw,nosuid,nodev', '--volume', f'{cache}:/cache:rw',
        '--env', 'RUST_LOG=off', '--entrypoint', '/usr/local/bin/librespot', arguments.image,
        '--name', name, '--device-id', receiver_id, '--system-cache', '/cache',
        '--disable-discovery', '--disable-audio-cache', '--backend', 'pipe',
        '--format', 'S16', '--dither', 'none', '--volume-ctrl', 'linear',
        '--initial-volume', '100', '--bitrate', '320', '--ap-port', '443',
    ]
    if linking:
        command.extend(['--enable-device-auth', '--device', '/dev/null'])
    return command


def stop_receiver(process, container_name):
    subprocess.run(['podman', 'stop', '--time', '2', container_name],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=20)
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
    if process.stdout:
        process.stdout.close()


def link(arguments, state, namespace):
    profile = identifier(arguments.profile)
    cache = private_directory(state / 'accounts' / profile)
    if (cache / 'credentials.json').exists():
        read_credentials(cache)
        print('This probe profile is already linked.')
        return 0
    container_name = f'syren-spotify-link-{uuid.uuid4().hex}'
    command = command_for(arguments, cache, device_id(namespace, profile, 'link'),
                          'SyrenSystem linking check', container_name, linking=True)
    print('Complete the Spotify pairing shown below using the intended Premium account.', flush=True)
    process = subprocess.Popen(command, stderr=subprocess.DEVNULL)
    try:
        deadline = time.monotonic() + 300
        while time.monotonic() < deadline:
            if (cache / 'credentials.json').exists():
                try:
                    read_credentials(cache)
                except json.JSONDecodeError:
                    time.sleep(0.1)
                    continue
                print('Account linked. Credentials remain in private local storage.')
                return 0
            if process.poll() is not None:
                raise RuntimeError('Spotify linking ended before credentials were saved')
            time.sleep(0.2)
        raise RuntimeError('Spotify linking timed out')
    finally:
        stop_receiver(process, container_name)


class AudioEvidence:
    def __init__(self):
        self.pending = b''
        self.buckets = {}

    def receive(self, payload, second):
        payload = self.pending + payload
        complete = len(payload) // 4 * 4
        self.pending = payload[complete:]
        samples = array.array('h')
        samples.frombytes(payload[:complete])
        if sys.byteorder != 'little':
            samples.byteswap()
        count, energy = self.buckets.get(second, (0, 0))
        self.buckets[second] = (count + len(samples), energy + sum(sample * sample for sample in samples))

    def active_seconds(self):
        return {second for second, (count, energy) in self.buckets.items()
                if count >= 44100 and math.sqrt(energy / count) >= 32}

    def summary(self):
        return {'samples': sum(count for count, _ in self.buckets.values()),
                'activeSeconds': sorted(self.active_seconds())}


class PcmPacer:
    def __init__(self):
        self.next_read = 0

    def read_size(self, now):
        return 3528 if now + 0.0000001 >= self.next_read else 0

    def consume(self, count, now):
        if count:
            if now > self.next_read + 0.020:
                self.next_read = now
            self.next_read += count / 176400


def longest_overlap(evidence):
    common = set.intersection(*(item.active_seconds() for item in evidence))
    longest = current = 0
    previous = None
    for second in sorted(common):
        current = current + 1 if previous == second - 1 else 1
        longest = max(longest, current)
        previous = second
    return longest


def interrupt_probe(signum, frame):
    raise KeyboardInterrupt


def prepare_receivers(arguments, state, namespace):
    profiles = [identifier(profile) for profile in arguments.profiles]
    if len(set(profiles)) != 2:
        raise ValueError('Two different linked probe profiles are required')
    credentials = [read_credentials(state / 'accounts' / profile) for profile in profiles]
    if credentials[0]['username'] == credentials[1]['username']:
        raise ValueError('Independent programmes require two different Spotify accounts')
    receivers = []
    for profile, account in zip(profiles, credentials):
        receiver_id = device_id(namespace, profile, arguments.destination_id)
        cache = private_directory(state / 'receivers' / receiver_id)
        credential_path = cache / 'credentials.json'
        descriptor = os.open(credential_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, 'w') as stream:
            os.fchmod(stream.fileno(), 0o600)
            json.dump(account, stream)
        receivers.append((profile, receiver_id, cache))
    return receivers


def run_probe(arguments, state, namespace):
    receivers = prepare_receivers(arguments, state, namespace)
    processes = []
    evidence = [AudioEvidence(), AudioEvidence()]
    pacers = [PcmPacer(), PcmPacer()]
    started = time.monotonic()
    failure = None
    image = subprocess.check_output(['podman', 'image', 'inspect', '--format', '{{.Id}}', arguments.image], text=True).strip()
    with selectors.DefaultSelector() as selector:
        try:
            for index, (_, receiver_id, cache) in enumerate(receivers):
                container_name = f'syren-spotify-probe-{uuid.uuid4().hex}'
                command = command_for(arguments, cache, receiver_id, arguments.name, container_name)
                process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
                processes.append((process, container_name))
                selector.register(process.stdout, selectors.EVENT_READ, index)
            print(f'In BOTH Spotify accounts, select "{arguments.name}" and play different programmes.', flush=True)
            print('SyrenApp must be closed. This probe measures audio privately and does not drive speakers.', flush=True)
            while time.monotonic() - started < arguments.seconds:
                for key, _ in selector.select(timeout=0.020):
                    elapsed = time.monotonic() - started
                    amount = pacers[key.data].read_size(elapsed)
                    if not amount:
                        continue
                    payload = os.read(key.fileobj.fileno(), amount)
                    if not payload:
                        raise RuntimeError('A personal receiver stopped before the probe completed')
                    pacers[key.data].consume(len(payload), elapsed)
                    evidence[key.data].receive(payload, int(elapsed))
                if any(process.poll() is not None for process, _ in processes):
                    raise RuntimeError('A personal receiver exited unexpectedly')
                time.sleep(0.002)
        except (RuntimeError, KeyboardInterrupt) as error:
            failure = str(error) or 'Probe interrupted'
        finally:
            for process, container_name in processes:
                stop_receiver(process, container_name)
    overlap = longest_overlap(evidence)
    report = {
        'schemaVersion': 2,
        'capturePacing': '44100 Hz, stereo, signed 16 bit PCM, real time',
        'imageId': image,
        'name': arguments.name,
        'destinationId': arguments.destination_id,
        'discoveryDisabled': True,
        'distinctAccounts': True,
        'receivers': [{'profile': profile, 'deviceId': receiver_id, **item.summary()}
                      for (profile, receiver_id, _), item in zip(receivers, evidence)],
        'concurrentAudioSeconds': overlap,
        'concurrentAudioPassed': failure is None and overlap >= 10,
        'failure': failure,
        'manualAcceptance': 'pending',
        'prerequisitePassed': False,
        'remainingChecks': ['Each account sees only its own destination under the same friendly name',
                            'The accounts played different programmes with SyrenApp closed',
                            'Repeat for a group destination and after renaming it',
                            'Record native device picker visibility after receiver restart and removal'],
    }
    output = state / f'report-{time.time_ns()}.json'
    output.write_text(json.dumps(report, indent=2) + '\n')
    print(f'Concurrent audio: {overlap} consecutive seconds. Evidence: {output}')
    print('Native visibility and different programme checks still require recorded human observations.')
    return 0 if report['concurrentAudioPassed'] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--state', type=Path, default=DEFAULT_STATE)
    parser.add_argument('--image', default=DEFAULT_IMAGE)
    commands = parser.add_subparsers(dest='command', required=True)
    linking = commands.add_parser('link')
    linking.add_argument('profile')
    running = commands.add_parser('run')
    running.add_argument('profiles', nargs=2)
    running.add_argument('--destination-id', default='house')
    running.add_argument('--name', default='SyrenSystem · Personal destination check')
    running.add_argument('--seconds', type=int, default=120)
    arguments = parser.parse_args()
    if arguments.command == 'run' and not 15 <= arguments.seconds <= 600:
        parser.error('Probe duration must be between 15 and 600 seconds')
    os.umask(0o077)
    signal.signal(signal.SIGTERM, interrupt_probe)
    try:
        state = private_directory(arguments.state.expanduser().resolve())
        namespace = namespace_for(state)
        if arguments.command == 'link':
            return link(arguments, state, namespace)
        identifier(arguments.destination_id)
        return run_probe(arguments, state, namespace)
    except (ValueError, OSError, RuntimeError, subprocess.SubprocessError):
        print('Probe failed. Check private cache permissions, account linking and the patched image.', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
