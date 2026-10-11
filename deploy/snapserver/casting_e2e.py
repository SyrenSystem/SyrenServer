#!/usr/bin/env python3
"""Play a test tone to one AirPlay target from this host and check each step up to the room receivers."""

import argparse
import json
import math
from pathlib import Path
import re
import signal
import struct
import subprocess
import sys
import tempfile
import time
import urllib.request
import uuid
import wave

# The naming code sits next to this file, and python3 -I leaves the script directory off the path.
sys.path.insert(0, str(Path(__file__).resolve().parent))

from casting_sessions import SLOT_COUNT, CastingSpec, casting_destinations, display_name, unique_names
from spotify_sessions import receiver_identity

PREFIX = 'SyrenSystem/v3/'
TAP_ID = 'casting-e2e-tap'
TONE_HERTZ = 440
TONE_SECONDS = 20
WAVE_RATE = 44100
TAP_RATE = 48000
TAP_FRAME_BYTES = 4


def progress(text):
    print(text, file=sys.stderr, flush=True)


def wait_for(check, seconds, description):
    deadline = time.monotonic() + seconds
    while True:
        result = check()
        if result:
            return result
        if time.monotonic() >= deadline:
            raise AssertionError(f'Waited {seconds} s for {description}')
        time.sleep(.25)


def write_tone(path):
    with wave.open(str(path), 'wb') as output:
        output.setnchannels(2)
        output.setsampwidth(2)
        output.setframerate(WAVE_RATE)
        frames = bytearray()
        for index in range(WAVE_RATE * TONE_SECONDS):
            value = int(16384 * math.sin(2 * math.pi * TONE_HERTZ * index / WAVE_RATE))
            frames += struct.pack('<hh', value, value)
        output.writeframes(frames)


def unescape(text):
    # avahi-browse writes most bytes that are not letters or digits as a backslash and three decimal digits.
    data = bytearray()
    index = 0
    while index < len(text):
        digits = text[index + 1:index + 4]
        if text[index] == '\\' and len(digits) == 3 and digits.isdigit():
            data.append(int(digits))
            index += 4
        elif text[index] == '\\' and index + 1 < len(text):
            data += text[index + 1].encode()
            index += 2
        else:
            data += text[index].encode()
            index += 1
    return data.decode('utf-8', 'replace')


def default_interface():
    # The default route with the lowest metric is the wired one on this host.
    routes = subprocess.run(['ip', '-4', 'route', 'show', 'default'], capture_output=True, text=True,
                            timeout=5, check=True).stdout
    candidates = []
    for line in routes.splitlines():
        device = re.search(r' dev (\S+)', line)
        metric = re.search(r' metric (\d+)', line)
        if device:
            candidates.append((int(metric.group(1)) if metric else 0, device.group(1)))
    if not candidates:
        raise AssertionError('No default route')
    return min(candidates)[1]


def browse():
    # Discovery is proven by the host Avahi, the responder that iOS and macOS see.
    return subprocess.run(['avahi-browse', '-rpt', '_raop._tcp'], capture_output=True, text=True,
                          timeout=20, check=True).stdout


def advertised(output, interface):
    services = []
    for line in output.splitlines():
        fields = line.split(';', 9)
        if len(fields) < 10 or fields[0] != '=' or fields[1] != interface or fields[2] != 'IPv4':
            continue
        device_id, _, name = unescape(fields[3]).partition('@')
        records = [unescape(item) for item in re.findall(r'"((?:[^"\\]|\\.)*)"', fields[9])]
        services.append({'name': name, 'address': fields[7], 'port': int(fields[8]), 'device_id': device_id, 'txt': records})
    return services


def destination_of(configuration, target, services):
    # The supervisor names targets in slot order, and each one listens on port 7000 plus its slot.
    planned = []
    for destination, name in casting_destinations(configuration):
        specification = CastingSpec(receiver_identity(configuration['stateId'], 'casting:airplay', destination),
                                    destination, display_name(name), 0)
        service = next((service for service in services
                        if service['device_id'].replace(':', '').upper() == specification.device_id), None)
        # A target that is not advertised sorts last, and the name check below catches a wrong guess.
        slot = service['port'] - specification.port if service else SLOT_COUNT + len(planned)
        planned.append((slot, specification, service))
    planned.sort(key=lambda plan: plan[0])
    for (slot, specification, service), name in zip(planned, unique_names([plan[1].name for plan in planned])):
        if name != target:
            continue
        if service is None or service['name'] != name:
            raise AssertionError(f'{target!r} belongs to destination {specification.destination}, '
                                 'but avahi-browse does not list it under that name on this interface over IPv4')
        if specification.destination == 'house':
            speakers = [speaker for group in configuration['groups'] if 'casting' in group['enabledSources']
                        for speaker in group['speakerIds']]
        else:
            speakers = next(list(group['speakerIds']) for group in configuration['groups']
                            if group['id'] == specification.destination)
        return specification.destination, speakers, service
    raise AssertionError(f'No AirPlay target of this household is called {target!r}')


def stream_for(device_id, streams):
    # The device ID is the first 12 hex digits of the identity, unicast and locally administered.
    wanted = device_id.replace(':', '').upper()
    for stream in streams:
        identity = stream['id'].removeprefix('airplay-')
        if stream['id'].startswith('airplay-') and len(identity) >= 12:
            top = (int(identity[:2], 16) & 0xFE) | 0x02
            if f'{top:02X}{identity[2:12].upper()}' == wanted:
                return stream['id']
    raise AssertionError(f'No Snapserver stream belongs to device {device_id}')


class Snapserver:
    def __init__(self, url):
        self.url = url

    def request(self, method, parameters=None):
        payload = json.dumps({'id': 1, 'jsonrpc': '2.0', 'method': method, 'params': parameters or {}}).encode()
        operation = urllib.request.Request(self.url, data=payload, headers={'Content-Type': 'application/json'})
        result = json.load(urllib.request.urlopen(operation, timeout=3))
        if 'error' in result:
            raise AssertionError(f'Snapserver refused {method}: {result["error"]}')
        return result['result']

    def status(self):
        return self.request('Server.GetStatus')['server']

    def stream_status(self, stream_id):
        return next((stream['status'] for stream in self.status()['streams'] if stream['id'] == stream_id), None)

    def tap_group(self):
        for group in self.status()['groups']:
            if any(client['id'] == TAP_ID and client['connected'] for client in group['clients']):
                return group['id']
        return None


class Broker:
    """Read and write the live broker through its container, as the other SyrenSystem checks do."""

    def __init__(self, container):
        self.container = container

    def retained(self, topic, default=None):
        result = subprocess.run(['podman', 'exec', self.container, 'mosquitto_sub', '-t', PREFIX + topic,
                                 '-C', '1', '-W', '3'], capture_output=True, text=True, timeout=15)
        lines = result.stdout.splitlines()
        if not lines:
            if default is not None:
                return default
            raise AssertionError(f'No retained {topic} message')
        return json.loads(lines[0])

    def command(self, payload):
        subscriber = subprocess.Popen(['podman', 'exec', self.container, 'mosquitto_sub', '-t',
                                       PREFIX + 'Result/' + payload['requestId'], '-C', '1', '-W', '10'],
                                      stdout=subprocess.PIPE, text=True)
        time.sleep(1)
        subprocess.run(['podman', 'exec', self.container, 'mosquitto_pub', '-t', PREFIX + 'Command', '-q', '1',
                        '-m', json.dumps(payload)], check=True, timeout=15)
        output, _ = subscriber.communicate(timeout=20)
        if not output.strip():
            raise AssertionError(f'No result for {payload["action"]}')
        return json.loads(output.splitlines()[0])


def open_session(broker, stream_id, destination):
    sessions = [session for session in broker.retained('Catalogue')['sessions']
                if session['source'] == 'casting' and session['destination'] == destination
                and session['state'] != 'ended'
                and any(transport['id'] == stream_id for transport in session.get('transports', []))]
    return max(sessions, key=lambda session: session['claimSequence'], default=None)


def session_by_id(broker, session_id):
    return next((session for session in broker.retained('Catalogue')['sessions'] if session['id'] == session_id),
                {'id': session_id, 'state': 'ended', 'eligible': False})


def check_session(session, stream_id, destination):
    transports = [transport for transport in session.get('transports', []) if transport['id'] == stream_id]
    problems = []
    if session.get('protocol') != 'airplay':
        problems.append('protocol is not airplay')
    if session.get('destination') != destination:
        problems.append('wrong destination')
    if session['state'] != 'playing' or not session['eligible']:
        problems.append(f"state {session['state']}, eligible {session['eligible']}")
    if not (session.get('ownerId') or '').startswith('guest:'):
        problems.append('the owner is not a guest')
    if not re.fullmatch(r'[0-9a-f]{64}', session.get('senderId') or ''):
        problems.append('the sender ID is not 64 hex digits')
    if not transports or transports[0]['kind'] != 'snapcast' or not transports[0]['available']:
        problems.append('no available snapcast transport')
    return problems


def playing_session(broker, session_id, stream_id, destination):
    session = session_by_id(broker, session_id)
    return None if check_session(session, stream_id, destination) else session


def silent_but_open(broker, session_id):
    session = session_by_id(broker, session_id)
    return session['state'] != 'ended' and not session['eligible']


def ended(broker, session_id):
    return session_by_id(broker, session_id)['state'] == 'ended'


def receivers_hear(broker, session_id, speakers):
    receivers = {receiver['speakerId']: receiver for receiver in broker.retained('Receivers')['receivers']}
    return all(speaker in receivers and receivers[speaker]['online']
               and session_id in receivers[speaker]['status'].get('receiving', [])
               and session_id in receivers[speaker]['status'].get('audible', []) for speaker in speakers)


def analyse(raw):
    # Zero crossings give the pitch, so S32 audio read as 16 bit shows up as a wrong pitch or broken channels.
    count = len(raw) // TAP_FRAME_BYTES
    samples = struct.unpack(f'<{count * 2}h', raw[:count * TAP_FRAME_BYTES])
    channels = []
    for channel in (0, 1):
        values = samples[channel::2]
        crossings = sum(1 for previous, current in zip(values, values[1:]) if (previous < 0) != (current < 0))
        power = sum(value * value for value in values) / max(len(values), 1)
        level = 20 * math.log10(max(math.sqrt(power), 1e-3) / 32768)
        channels.append({'hertz': round(crossings / 2 / (len(values) / TAP_RATE), 1), 'dbfs': round(level, 1)})
    return channels


def start_sender(arguments, found, tone, log):
    command = [arguments.pyatv, '--manual', '--address', found['address'], '--port', str(found['port']),
               '--protocol', 'raop', '--id', found['device_id'], '--service-properties', ';'.join(found['txt']),
               f'set_volume={arguments.volume}', f'stream_file={tone}']
    return subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)


def stop(process):
    if process is not None and process.poll() is None:
        process.kill()
        process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pyatv', required=True, help='path to atvremote')
    parser.add_argument('--snapclient', required=True)
    parser.add_argument('--target', required=True,
                        help='the advertised AirPlay name, for example "SyrenSystem · Kantoor" or "SyrenSystem · Kantoor #2"')
    parser.add_argument('--interface', help='the interface whose IPv4 address is streamed to')
    parser.add_argument('--volume', type=int, default=100, help='sender volume in percent')
    parser.add_argument('--snapserver', default='http://127.0.0.1:1780/jsonrpc')
    parser.add_argument('--mosquitto-container', default='syrenserver_mosquitto_1')
    arguments = parser.parse_args()
    interface = arguments.interface or default_interface()
    snapserver = Snapserver(arguments.snapserver)
    broker = Broker(arguments.mosquitto_container)
    destination, speakers, found = destination_of(broker.retained('Configuration'), arguments.target,
                                                  advertised(browse(), interface))
    summary = {'target': arguments.target, 'destination': destination, 'interface': interface}
    with tempfile.TemporaryDirectory(prefix='syren-casting-e2e-') as temporary:
        directory = Path(temporary)
        tone = directory / 'tone.wav'
        tap_file = directory / 'tap.raw'
        write_tone(tone)
        stream_id = stream_for(found['device_id'], snapserver.status()['streams'])
        summary.update(address=found['address'], port=found['port'], stream=stream_id)
        progress(f'{arguments.target} is {found["address"]}:{found["port"]}, stream {stream_id}')
        tap = sender = None
        with (directory / 'tools.log').open('w') as log:
            try:
                tap = subprocess.Popen([arguments.snapclient, '--host', '127.0.0.1', '--hostID', TAP_ID,
                                        '--player', f'file:filename={tap_file}', '--sampleformat', '48000:16:2'],
                                       stdout=log, stderr=subprocess.STDOUT)
                group = wait_for(snapserver.tap_group, 10, 'the tap client')
                snapserver.request('Group.SetStream', {'id': group, 'stream_id': stream_id})

                started = time.monotonic()
                sender = start_sender(arguments, found, tone, log)
                wait_for(lambda: snapserver.stream_status(stream_id) == 'playing', 5, 'the first audio')
                summary['first_audio_seconds'] = round(time.monotonic() - started, 2)
                progress(f'First audio after {summary["first_audio_seconds"]} s')
                session = wait_for(lambda: open_session(broker, stream_id, destination), 10, 'the casting session')
                session = wait_for(lambda: playing_session(broker, session['id'], stream_id, destination), 10,
                                   'a playing, eligible guest session')
                summary['session'] = session['id']
                wait_for(lambda: receivers_hear(broker, session['id'], speakers), 10,
                         f'receivers {speakers} to hear the session')
                progress('The session plays and every receiver in the room hears it')

                time.sleep(5)
                window = tap_file.read_bytes()[-2 * TAP_RATE * TAP_FRAME_BYTES:]
                summary['channels'] = analyse(window)
                for channel in summary['channels']:
                    if abs(channel['hertz'] - TONE_HERTZ) > TONE_HERTZ * .02 or channel['dbfs'] <= -40:
                        raise AssertionError(f'The tap does not hear a clean {TONE_HERTZ} Hz tone: {summary["channels"]}')
                progress(f'The tap hears {summary["channels"]}')

                # A stopped sender keeps its connection open, like a phone that walked out of range.
                sender.send_signal(signal.SIGSTOP)
                wait_for(lambda: silent_but_open(broker, session['id']), 10, 'the silent session to become ineligible')
                sender.kill()
                sender.wait(timeout=5)
                wait_for(lambda: ended(broker, session['id']), 7, 'the killed sender to end its session')
                progress('A silent sender stops winning the room, and a closed one ends its session')

                sender = start_sender(arguments, found, tone, log)
                wait_for(lambda: snapserver.stream_status(stream_id) == 'playing', 5, 'the second stream')
                second = wait_for(lambda: open_session(broker, stream_id, destination), 10, 'the second session')
                sender.wait(timeout=TONE_SECONDS + 20)
                wait_for(lambda: ended(broker, second['id']), 7, 'the finished stream to end its session')
                summary['finished_session'] = second['id']
                progress('A stream that finishes ends its session')

                # Only a sender with a DACP ID and a name is listed; pyatv may send either kind.
                configuration = broker.retained('Configuration')
                devices = broker.retained('CastingDevices', {'devices': []})['devices']
                listed = [device for device in devices if device['id'] in (session['senderId'], second['senderId'])]
                for device in listed:
                    result = broker.command({'requestId': str(uuid.uuid4()), 'protocolVersion': 3,
                                             'stateId': configuration['stateId'],
                                             'generation': configuration['generation'],
                                             'expectedRevision': configuration['revision'],
                                             'action': 'forgetCasting', 'deviceId': device['id']})
                    if not result.get('success'):
                        raise AssertionError(f'forgetCasting failed: {result.get("error")}')
                    configuration = broker.retained('Configuration')
                summary['forgotten_devices'] = len(listed)
            except Exception:
                log.flush()
                progress((directory / 'tools.log').read_text())
                raise
            finally:
                stop(sender)
                stop(tap)
                try:
                    wait_for(lambda: snapserver.tap_group() is None, 5, 'the tap to disconnect')
                    snapserver.request('Server.DeleteClient', {'id': TAP_ID})
                except (AssertionError, OSError) as error:
                    progress(f'Could not delete the tap client: {error}')
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == '__main__':
    main()
