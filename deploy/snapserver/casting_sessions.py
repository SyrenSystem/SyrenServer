"""Plan AirPlay destinations and turn shairport-sync metadata into lifecycle events."""

import base64
import binascii
from dataclasses import dataclass
import math
from pathlib import Path
import re
import time
import unicodedata

from spotify_sessions import LifecycleJournal, destinations, realtime_command

# Below Snapserver at 85, like librespot.
SHAIRPORT_PRIORITY = 84
RUNTIME_DIRECTORY = Path('/tmp/syren-casting')
RELAY_SOCKET = '/run/syren-avahi-relay/system_bus_socket'
SLOT_COUNT = 16
NAME_BYTES = 50
UDP_PORT_RANGE = 10
SAMPLE_FORMAT = '48000:16:2'
OUTPUT_DESCRIPTION = '48000/S16_LE/2'
METADATA_LIMIT = 1024 * 1024
IDENTITY_WAIT = 1.0
RECONNECT_GRACE = 5
LEVEL_WAIT = 4
# The sender volume runs from -30 to 0, and -144 means mute.
VOLUME_RANGE_DB = 60
MUTE_VOLUME = -144.0
# Mute keeps a tiny gain (about -120 dB), because a receiver hands a room with zero gain to another session.
MUTE_GAIN = 0.000001

# The instance gets nothing under /var/lib/snapserver, where the Spotify credentials and journals live.
# These are file rules only, so the instance can still reach the network.
LANDLOCK_RULES = (
    ('execute,read-file,read-dir', '/usr'),
    ('read-file,read-dir', '/etc'),
    ('read-file,read-dir', '/proc'),
    ('read-file,read-dir', '/sys'),
    ('read-file,write-file,read-dir', '/dev'),
    ('read-file,write-file,read-dir,make-fifo', str(RUNTIME_DIRECTORY)),
    ('read-dir', '/run/syren-avahi-relay'),
)


@dataclass(frozen=True)
class CastingSpec:
    identity: str
    destination: str
    name: str
    slot: int
    runtime: Path = RUNTIME_DIRECTORY
    # The journal reads these, and casting owners come from the sender instead.
    profile = None
    account = None

    @property
    def stream(self):
        return 'airplay-' + self.identity

    @property
    def fifo(self):
        return Path(self.runtime) / (self.stream + '.fifo')

    @property
    def meta(self):
        return Path(self.runtime) / (self.stream + '.meta')

    @property
    def config(self):
        return Path(self.runtime) / (self.stream + '.conf')

    @property
    def port(self):
        return 7000 + self.slot

    @property
    def udp_port_base(self):
        return 6001 + UDP_PORT_RANGE * self.slot

    @property
    def device_id(self):
        # A unicast, locally administered MAC address that never changes with the name.
        top = (int(self.identity[:2], 16) & 0xFE) | 0x02
        return f'{top:02X}{self.identity[2:12].upper()}'

    @property
    def shm_name(self):
        return f'/syren-airplay-{self.slot}'


def casting_destinations(configuration):
    # SyrenSystem itself is an AirPlay target only while at least one room has AirPlay on.
    if not any('casting' in group['enabledSources'] for group in configuration['groups']):
        return []
    return destinations(configuration, 'casting')


def display_name(name):
    # shairport-sync expands %h, %H, %v and %V with no escape, so a percent sign becomes a full width one.
    name = ''.join(' ' if unicodedata.category(character) == 'Cc' else character for character in name.replace('%', '％'))
    # shairport-sync cuts names at 50 bytes, so cut first on a character boundary.
    return name.encode()[:NAME_BYTES].decode('utf-8', 'ignore')


def unique_names(names):
    # Avahi refuses a second local service with the same name in any case, and the losing instance exits, so a repeat gets a number.
    given, unique = set(), []
    for name in names:
        candidate, number = name, 1
        while candidate.lower() in given:
            number += 1
            suffix = f' #{number}'
            candidate = name.encode()[:NAME_BYTES - len(suffix)].decode('utf-8', 'ignore').rstrip() + suffix
        given.add(candidate.lower())
        unique.append(candidate)
    return unique


def config_string(value):
    # A config file expands ${NAME}, and $${ keeps a literal ${.
    return '"' + value.replace('\\', '\\\\').replace('"', '\\"').replace('${', '$${') + '"'


def allocate_slots(slots, state_id, wanted):
    # Each destination keeps its slot, and a new one takes the lowest free slot.
    known = slots.get(state_id) if isinstance(slots, dict) else None
    known = known if isinstance(known, dict) else {}
    allocated, used, skipped = {}, set(), []
    for identity, destination, name in wanted:
        entry = known.get(identity)
        slot = entry.get('slot') if isinstance(entry, dict) else None
        if isinstance(slot, int) and 0 <= slot < SLOT_COUNT and slot not in used:
            allocated[identity] = {'slot': slot, 'destination': destination, 'name': name}
            used.add(slot)
    for identity, destination, name in wanted:
        if identity in allocated:
            continue
        free = next((slot for slot in range(SLOT_COUNT) if slot not in used), None)
        if free is None:
            skipped.append(identity)
        else:
            allocated[identity] = {'slot': free, 'destination': destination, 'name': name}
            used.add(free)
    return {state_id: allocated}, skipped


def render_config(specification, log_verbosity=0):
    return f'''// Generated by casting_supervisor.py for one destination. Do not edit.
general = {{
  name = {config_string(display_name(specification.name))};
  service_type = "airplay2";
  port = {specification.port};
  udp_port_base = {specification.udp_port_base};
  udp_port_range = {UDP_PORT_RANGE};
  airplay_device_id = 0x{specification.device_id}L;
  nqptp_shared_memory_interface_name = {config_string(specification.shm_name)};
  mdns_backend = "avahi";
  output_backend = "pipe";
  ignore_volume_control = "yes";
  volume_range_db = {VOLUME_RANGE_DB};
  volume_control_profile = "standard";
  audio_backend_latency_offset_in_seconds = 0.0;
  audio_decoded_buffer_desired_length_in_seconds = 5.0;
  six_channel_mode = "off";
  eight_channel_mode = "off";
  playback_mode = "stereo";
}};
sessioncontrol = {{
  allow_session_interruption = "yes";
  session_timeout = 60;
}};
pipe = {{
  name = {config_string(str(specification.fifo))};
  output_rate = 48000;
  output_format = "S16_LE";
  output_channels = 2;
}};
metadata = {{
  enabled = "yes";
  include_cover_art = "no";
  pipe_name = {config_string(str(specification.meta))};
  progress_interval = 0.0;
}};
diagnostics = {{
  log_verbosity = {int(log_verbosity)};
}};
'''


def instance_command(specification, executable='/usr/local/bin/shairport-sync'):
    command = ['/usr/bin/setpriv', '--pdeathsig', 'KILL', '--landlock-access', 'fs']
    for access, path in LANDLOCK_RULES:
        if path == str(RUNTIME_DIRECTORY):
            path = str(specification.runtime)
        command += ['--landlock-rule', f'path-beneath:{access}:{path}']
    return command + ['--', *realtime_command([executable, '-c', str(specification.config)], SHAIRPORT_PRIORITY)]


class CastingJournal(LifecycleJournal):
    """The Spotify journal with source casting, which keeps a device key only while its session is open."""

    def __init__(self, path):
        super().__init__(path, source='casting')
        if any(receiver['sessionId'] is None and receiver['accountId'] is not None
               for receiver in self.state['receivers'].values()):
            self.save()

    def save(self):
        # The reconnect rule compares keys only during a session, and the name leaves with the start event once acknowledged.
        for receiver in self.state['receivers'].values():
            if receiver['sessionId'] is None:
                receiver['accountId'] = None
        super().save()


ITEM = re.compile(rb'<item><type>([0-9a-fA-F]{1,8})</type><code>([0-9a-fA-F]{1,8})</code><length>([0-9]{1,10})</length>'
                  rb'(?:\n<data encoding="base64">\n([A-Za-z0-9+/=\r\n]*)</data>)?</item>')


def airplay_attenuation(volume, max_db=0, min_db=-100 * VOLUME_RANGE_DB):
    # The standard profile of shairport-sync (vol2attn in common.c), in hundredths of a dB.
    if not -30.0 <= volume <= 0.0:
        return min_db
    range_db = max_db - min_db
    first_slope = -(range_db // 2)
    if -range_db > first_slope:
        first_slope = range_db
    lines = ((0, first_slope), (-5, first_slope - (range_db + first_slope) / 2), (-17, -range_db))
    setting = 0
    for start, end in lines:
        if volume <= start:
            setting = min(setting, end * (volume - start) / (-30 - start))
    return setting + max_db


def airplay_gain(volume):
    # The linear gain shairport-sync applied itself before it ignored the volume, so loudness stays the same.
    if volume == MUTE_VOLUME:
        return MUTE_GAIN
    return 10 ** (airplay_attenuation(volume) / 2000)


def parse_volume(data):
    # A pvol item starts with the sender volume, and the dB fields after it are zero when shairport-sync ignores it.
    try:
        volume = float((data or b'').decode('ascii').split(',')[0])
    except (UnicodeDecodeError, ValueError):
        return None
    return volume if math.isfinite(volume) else None


def metadata_code(value):
    return bytes.fromhex(value.decode().zfill(8)).decode('latin-1')


class MetadataParser:
    def __init__(self, limit=METADATA_LIMIT):
        self.limit = limit
        self.buffer = b''

    def feed(self, data):
        self.buffer += data
        items = []
        while True:
            start = self.buffer.find(b'<item>')
            if start < 0:
                # Keep a tail that may be the start of the next item.
                self.buffer = self.buffer[-5:]
                return items
            end = self.buffer.find(b'</item>', start)
            if end < 0:
                self.buffer = self.buffer[start:]
                if len(self.buffer) <= self.limit:
                    return items
                following = self.buffer.find(b'<item>', 1)
                self.buffer = self.buffer[following:] if following > 0 else b''
                continue
            end += len(b'</item>')
            match = ITEM.fullmatch(self.buffer, start, end)
            if match is None:
                following = self.buffer.find(b'<item>', start + 1, end)
                self.buffer = self.buffer[following if following >= 0 else end:]
                continue
            self.buffer = self.buffer[end:]
            try:
                payload = None if match[4] is None else base64.b64decode(match[4])
            except binascii.Error:
                continue
            items.append((metadata_code(match[1]), metadata_code(match[2]), payload))


@dataclass(frozen=True)
class AdapterEvent:
    # Kinds: sender, identity, play, pause, stream_up, stream_down, output, released, level and volume.
    kind: str
    value: str | float | None = None
    field: str | None = None
    delay: float = 0.0


SEGMENTED_STREAMS = ('Classic', 'Realtime')


class AirPlayAdapter:
    def __init__(self):
        self.previous = ()
        self.dacp = None
        self.stream_type = None

    def feed(self, kind, code, data):
        previous = self.previous
        self.previous = (previous + (code,))[-2:]
        if kind != 'ssnc':
            return []
        value = data.decode('utf-8', 'replace') if data else ''
        if code == 'conn':
            self.stream_type = None
            return [AdapterEvent('sender', value)]
        if code == 'snam':
            return [AdapterEvent('identity', value, 'name')]
        if code == 'cdid':
            return [AdapterEvent('identity', value.lower(), 'device')]
        if code == 'cmac':
            # The MAC address is the last item of an AirPlay 2 SETUP, and it is never a key.
            return [AdapterEvent('identity')]
        if code == 'daid':
            self.dacp = value
            return []
        if code == 'svip' and previous[-1:] == ('clip',) and previous[:1] != ('conn',):
            # A clip and svip pair that does not follow conn ends a classic SETUP.
            events = [AdapterEvent('identity', self.dacp.upper(), 'dacp')] if previous[:1] == ('daid',) and self.dacp else []
            return events + [AdapterEvent('identity')]
        if code == 'styp':
            self.stream_type = value
            return [AdapterEvent('play')] if value in SEGMENTED_STREAMS else []
        if code == 'pres' or (code == 'prsm' and self.stream_type in SEGMENTED_STREAMS):
            return [AdapterEvent('play')]
        if code == 'paus':
            return [AdapterEvent('pause', delay=1.0)]
        if code == 'pfls' and self.stream_type in SEGMENTED_STREAMS:
            return [AdapterEvent('pause', delay=2.0)]
        if code == 'pbeg':
            return [AdapterEvent('stream_up')]
        if code == 'pend':
            return [AdapterEvent('stream_down')]
        if code == 'odsc':
            return [AdapterEvent('output', value)]
        if code == 'pvol':
            volume = parse_volume(data)
            return [] if volume is None else [AdapterEvent('volume', round(airplay_gain(volume), 6))]
        if code in ('prmp', 'syen'):
            self.stream_type = self.dacp = None
            return [AdapterEvent('released')]
        return []


class CastingMapper:
    def __init__(self, journal, specification, protocol='airplay', clock=time.monotonic, log=print,
                 expected_output=OUTPUT_DESCRIPTION):
        self.journal = journal
        self.specification = specification
        self.protocol = protocol
        self.clock = clock
        self.log = log
        self.expected_output = expected_output
        self.reset()

    def reset(self):
        self.principal = None
        self.hold_until = None
        self.stream = None
        self.wrong_output = False
        # The sender volume as a linear gain, or None until the sender gives one.
        self.volume = None
        self.clear_session()

    def clear_session(self):
        self.intent = None
        self.announced = False
        self.tripped = False
        self.pending_pause = None
        self.level_since = None

    @property
    def session(self):
        return self.journal.state['receivers'].get(self.specification.identity, {}).get('sessionId')

    @property
    def connected(self):
        return self.principal is not None

    @property
    def idle(self):
        return self.principal is None and self.session is None and self.hold_until is None

    @property
    def available(self):
        return self.stream is not False and self.hold_until is None and not self.tripped and not self.wrong_output

    def emit(self, action, account=None, details=None):
        self.journal.emit(self.specification, action, account, details)
        if action == 'end':
            self.clear_session()
            return
        self.announced = action != 'interrupt'
        if action in ('play', 'pause'):
            self.intent = action
        if action != 'interrupt':
            self.level_since = self.clock()

    def sync(self):
        # The server only sees availability changes, so a repeated state sends nothing.
        if self.session is None:
            self.hold_until = None
            self.clear_session()
        elif self.available != self.announced:
            self.emit('transport' if self.available else 'interrupt')

    def handle(self, event):
        now = self.clock()
        if event.kind == 'level':
            self.observe_level(event.value, now)
        elif event.kind == 'sender':
            if self.principal is not None:
                self.release(now)
            self.principal = {'address': event.value, 'fields': {}, 'since': now, 'key': None, 'queue': []}
            self.stream = None
            # The output format and volume belong to one sender, so a new sender starts without the old ones.
            self.wrong_output = False
            self.volume = None
        elif self.principal is None:
            return
        elif event.kind == 'volume':
            # The volume is never a lifecycle event, so it waits for no identity and only goes out with heartbeats.
            self.volume = event.value
        elif event.kind == 'identity':
            if self.principal['key'] is not None:
                return
            if event.field is None:
                self.resolve(now)
            else:
                self.principal['fields'][event.field] = event.value
        elif event.kind == 'released':
            self.release(now)
        elif self.principal['key'] is None:
            self.principal['queue'].append(event)
            return
        else:
            self.apply(event, now)
        self.sync()

    def resolve(self, now):
        fields = self.principal['fields']
        kind = next((kind for kind in ('device', 'dacp') if fields.get(kind)), 'address')
        value = fields[kind] if kind != 'address' else self.principal['address']
        key = f'{self.protocol}:{kind}:{value}'
        self.principal['key'] = key
        receiver = self.journal.state['receivers'].get(self.specification.identity, {})
        if self.session is not None and receiver.get('accountId') == key:
            # The same device is back within the grace, so it keeps its session and claim.
            self.hold_until = None
        else:
            if self.session is not None:
                self.hold_until = None
                self.emit('end')
            details = {'protocol': self.protocol}
            if kind != 'address' and fields.get('name'):
                details['accountName'] = fields['name']
            self.emit('start', key, details)
        queue, self.principal['queue'] = self.principal['queue'], []
        for event in queue:
            self.apply(event, now)

    def release(self, now):
        self.principal = None
        self.pending_pause = None
        self.stream = None
        if self.session is not None and self.hold_until is None:
            self.hold_until = now + RECONNECT_GRACE

    def apply(self, event, now):
        if event.kind == 'play':
            self.pending_pause = None
            self.level_since = now
            self.tripped = False
            if self.intent != 'play':
                self.emit('play')
        elif event.kind == 'pause':
            self.pending_pause = now + event.delay
        elif event.kind == 'stream_up':
            self.stream = True
        elif event.kind == 'stream_down':
            self.stream = False
        elif event.kind == 'output':
            # Each output description replaces the last one, so a matching format restores the transport.
            wrong = event.value != self.expected_output
            if wrong and not self.wrong_output:
                self.log(f'Casting stream {self.specification.stream} has output {event.value} instead of {self.expected_output}')
            self.wrong_output = wrong

    def observe_level(self, status, now):
        if status is None:
            return
        if status == 'playing':
            self.level_since = now
            self.tripped = False
        elif self.intent == 'play' and self.session is not None and self.available:
            # A sender that left without a word stops winning the room, but keeps its claim.
            if now - (self.level_since if self.level_since is not None else now) >= LEVEL_WAIT:
                self.tripped = True

    def tick(self):
        now = self.clock()
        if self.principal is not None and self.principal['key'] is None and now >= self.principal['since'] + IDENTITY_WAIT:
            self.resolve(now)
        if self.pending_pause is not None and now >= self.pending_pause:
            self.pending_pause = None
            if self.session is not None and self.intent != 'pause':
                self.emit('pause')
        if self.hold_until is not None and now >= self.hold_until:
            self.hold_until = None
            if self.session is not None:
                self.emit('end')
        self.sync()

    def reconcile(self):
        if self.session is not None and self.intent in ('play', 'pause') and self.available:
            self.emit('reconcile')

    def stop(self):
        # An instance that dies or restarts cannot keep its AirPlay connection.
        if self.session is not None:
            self.emit('end')
        self.reset()


TRACE_KINDS = {'conn': 'ip', 'clip': 'ip', 'svip': 'ip', 'snam': 'name', 'cdid': 'cdid', 'cmac': 'mac',
               'cmod': 'model', 'daid': 'daid', 'acre': 'acre'}
TRACE_VALUES = ('styp', 'sdsc', 'odsc', 'pvol')


class TraceFormatter:
    def __init__(self):
        self.placeholders = {}
        self.counts = {}

    def format(self, slot, kind, code, data):
        line = f'airplay[{slot}] {code} {len(data or b"")}'
        if kind != 'ssnc' or not data:
            return line
        if code in TRACE_KINDS:
            # Identity values become stable placeholders, so a trace never holds private material.
            placeholder_kind = TRACE_KINDS[code]
            key = (placeholder_kind, data)
            if key not in self.placeholders:
                self.counts[placeholder_kind] = self.counts.get(placeholder_kind, 0) + 1
                self.placeholders[key] = f'<{placeholder_kind} {self.counts[placeholder_kind]}>'
            return line + ' ' + self.placeholders[key]
        if code in TRACE_VALUES:
            return line + ' ' + data.decode('utf-8', 'replace')
        return line
