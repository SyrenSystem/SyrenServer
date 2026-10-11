import base64
import json
import re
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from casting_sessions import (LANDLOCK_RULES, SAMPLE_FORMAT, SHAIRPORT_PRIORITY, AdapterEvent, AirPlayAdapter, CastingJournal,
                              CastingMapper, CastingSpec, MetadataParser, TraceFormatter, allocate_slots,
                              casting_destinations, display_name, instance_command, render_config)
from spotify_sessions import LifecycleJournal, receiver_identity

# Every key the pinned shairport-sync reads from these sections (shairport.c, audio.c and the pipe backend).
KNOWN_KEYS = {
    'general.name', 'general.service_type', 'general.port', 'general.udp_port_base', 'general.udp_port_range',
    'general.airplay_device_id', 'general.nqptp_shared_memory_interface_name', 'general.mdns_backend',
    'general.output_backend', 'general.ignore_volume_control', 'general.volume_range_db', 'general.volume_control_profile',
    'general.audio_backend_latency_offset_in_seconds', 'general.audio_decoded_buffer_desired_length_in_seconds',
    'general.six_channel_mode', 'general.eight_channel_mode', 'general.playback_mode', 'general.interpolation',
    'sessioncontrol.allow_session_interruption', 'sessioncontrol.session_timeout',
    'pipe.name', 'pipe.output_rate', 'pipe.output_format', 'pipe.output_channels',
    'metadata.enabled', 'metadata.include_cover_art', 'metadata.pipe_name', 'metadata.progress_interval',
    'diagnostics.log_verbosity',
}
IDENTITY = '7b3f19c2d4e1aa00bb11cc22dd33ee44ff556677'


def item(code, value=None, kind='ssnc'):
    data = value.encode() if isinstance(value, str) else value
    text = f'<item><type>{kind.encode().hex()}</type><code>{code.encode().hex()}</code><length>{len(data or b"")}</length>'
    if data:
        text += '\n<data encoding="base64">\n' + base64.b64encode(data).decode() + '</data>'
    return (text + '</item>\n').encode()


def connect(device='A1:B2:C3:D4:E5:F6', name="Yme's iPhone", address='192.0.2.10'):
    return [('conn', address), ('clip', address), ('svip', '192.0.2.53'), ('snam', name), ('cdid', device),
            ('cmod', 'iPhone17,1'), ('cmac', '02:11:22:33:44:55')]


def parse_config(text):
    values, section = {}, None
    for line in text.splitlines():
        line = line.strip()
        if match := re.fullmatch(r'(\w+) = \{', line):
            section = match[1]
        elif match := re.fullmatch(r'(\w+) = (.*);', line):
            values[f'{section}.{match[1]}'] = match[2]
    return values


def config_value(text):
    # Undo what shairport-sync does to a quoted value: expand $${ first, then the libconfig escapes.
    return re.sub(r'\\(.)', r'\1', text[1:-1].replace('$${', '${'))


class Clock:
    def __init__(self):
        self.now = 1000.0

    def __call__(self):
        return self.now


class PlanTests(unittest.TestCase):
    def test_no_target_until_a_group_enables_casting(self):
        configuration = {'groups': [{'id': 'kitchen', 'name': 'Kitchen', 'enabledSources': ['spotify', 'laptop']}]}
        self.assertEqual([], casting_destinations(configuration))
        configuration['groups'].append({'id': 'office', 'name': 'Kantoor', 'enabledSources': ['casting']})
        self.assertEqual([('house', 'SyrenSystem'), ('office', 'SyrenSystem · Kantoor')], casting_destinations(configuration))

    def test_identity_device_id_and_clock_name_survive_a_rename(self):
        identity = receiver_identity('home', 'casting:airplay', 'office')
        self.assertEqual(identity, receiver_identity('home', 'casting:airplay', 'office'))
        self.assertNotEqual(identity, receiver_identity('home', 'guest:office', 'office'))
        self.assertEqual(40, len(identity))
        before, skipped = allocate_slots({}, 'home', [(identity, 'office', 'SyrenSystem · Kantoor')])
        after, skipped = allocate_slots(before, 'home', [(identity, 'office', 'SyrenSystem · Werkkamer')])
        self.assertEqual(before['home'][identity]['slot'], after['home'][identity]['slot'])
        self.assertEqual('SyrenSystem · Werkkamer', after['home'][identity]['name'])
        old = CastingSpec(identity, 'office', 'SyrenSystem · Kantoor', before['home'][identity]['slot'])
        new = CastingSpec(identity, 'office', 'SyrenSystem · Werkkamer', after['home'][identity]['slot'])
        self.assertEqual((old.device_id, old.shm_name, old.port, old.stream), (new.device_id, new.shm_name, new.port, new.stream))
        changed = [(left, right) for left, right in zip(render_config(old).splitlines(), render_config(new).splitlines())
                   if left != right]
        self.assertEqual([('  name = "SyrenSystem · Kantoor";', '  name = "SyrenSystem · Werkkamer";')], changed)
        specification = CastingSpec(IDENTITY, 'office', 'SyrenSystem', 1)
        self.assertEqual('7A3F19C2D4E1', specification.device_id)
        self.assertEqual('/syren-airplay-1', specification.shm_name)
        self.assertIn('  airplay_device_id = 0x7A3F19C2D4E1L;', render_config(specification))
        for top in range(256):
            device_id = CastingSpec(f'{top:02x}' + IDENTITY[2:], 'office', 'SyrenSystem', 0).device_id
            self.assertEqual((0, 2), (int(device_id[:2], 16) & 1, int(device_id[:2], 16) & 2))

    def test_slots_are_stable_and_reused_after_removal(self):
        wanted = [('first', 'house', 'SyrenSystem'), ('second', 'kitchen', 'K'), ('third', 'office', 'O')]
        slots, skipped = allocate_slots({}, 'home', wanted)
        self.assertEqual({'first': 0, 'second': 1, 'third': 2}, {key: entry['slot'] for key, entry in slots['home'].items()})
        slots, skipped = allocate_slots(slots, 'home', [wanted[0], wanted[2]])
        self.assertEqual({'first': 0, 'third': 2}, {key: entry['slot'] for key, entry in slots['home'].items()})
        slots, skipped = allocate_slots(slots, 'home', [wanted[0], wanted[2], ('fourth', 'hall', 'H')])
        self.assertEqual(1, slots['home']['fourth']['slot'])
        self.assertEqual({'slot': 1, 'destination': 'hall', 'name': 'H'}, slots['home']['fourth'])
        slots, skipped = allocate_slots(slots, 'rebuilt', [('fifth', 'house', 'SyrenSystem')])
        self.assertEqual({'rebuilt': {'fifth': {'slot': 0, 'destination': 'house', 'name': 'SyrenSystem'}}}, slots)
        many = [(f'target-{index}', f'group-{index}', str(index)) for index in range(18)]
        slots, skipped = allocate_slots({}, 'home', many)
        self.assertEqual(list(range(16)), sorted(entry['slot'] for entry in slots['home'].values()))
        self.assertEqual(['target-16', 'target-17'], skipped)
        slots, skipped = allocate_slots({'home': {'broken': {'slot': 99}, 'twin': {'slot': 0}, 'other': {'slot': 0}}}, 'home',
                                        [('broken', 'a', 'A'), ('twin', 'b', 'B'), ('other', 'c', 'C')])
        self.assertEqual({'twin': 0, 'broken': 1, 'other': 2}, {key: entry['slot'] for key, entry in slots['home'].items()})

    def test_each_slot_gets_its_own_udp_port_range(self):
        specifications = [CastingSpec(IDENTITY, 'house', 'SyrenSystem', slot) for slot in range(16)]
        self.assertEqual(list(range(7000, 7016)), [specification.port for specification in specifications])
        ranges = [set(range(specification.udp_port_base, specification.udp_port_base + 10)) for specification in specifications]
        self.assertEqual(160, len(set().union(*ranges)))
        self.assertEqual((6001, 6160), (min(ranges[0]), max(ranges[-1])))
        values = parse_config(render_config(specifications[3]))
        self.assertEqual(('7003', '6031', '10'), (values['general.port'], values['general.udp_port_base'],
                                                   values['general.udp_port_range']))

    def test_names_are_truncated_on_a_character_boundary_and_escaped(self):
        long_name = 'SyrenSystem · ' + 'é' * 30
        shown = display_name(long_name)
        self.assertLessEqual(len(shown.encode()), 50)
        self.assertTrue(long_name.startswith(shown))
        self.assertEqual(49, len(shown.encode()))
        self.assertEqual('SyrenSystem · ％h ％V', display_name('SyrenSystem · %h %V'))
        self.assertEqual('Kitchen Hall', display_name('Kitchen\nHall'))
        self.assertEqual(display_name(long_name), display_name(display_name(long_name)))
        name = 'A "quoted" \\ ${HOME} $${ %H'
        specification = CastingSpec(IDENTITY, 'office', name, 0)
        written = parse_config(render_config(specification))['general.name']
        self.assertEqual(r'"A \"quoted\" \\ $${HOME} $$${ ％H"', written)
        self.assertEqual(display_name(name), config_value(written))

    def test_config_pins_the_pipe_to_the_stream_format_and_has_no_unknown_keys(self):
        specification = CastingSpec(IDENTITY, 'office', 'SyrenSystem · Kantoor', 1)
        text = render_config(specification)
        values = parse_config(text)
        self.assertEqual(set(), set(values) - KNOWN_KEYS)
        self.assertNotIn('pipe_timeout', text)
        self.assertEqual(f'"{specification.fifo}"', values['pipe.name'])
        self.assertEqual(('48000', '"S16_LE"', '2'), (values['pipe.output_rate'], values['pipe.output_format'],
                                                      values['pipe.output_channels']))
        self.assertEqual('48000:16:2', SAMPLE_FORMAT)
        self.assertEqual(f'"{specification.meta}"', values['metadata.pipe_name'])
        self.assertEqual(('"airplay2"', '"pipe"', '"avahi"'), (values['general.service_type'], values['general.output_backend'],
                                                              values['general.mdns_backend']))
        self.assertEqual('"/syren-airplay-1"', values['general.nqptp_shared_memory_interface_name'])
        self.assertEqual(('0', '1'), (parse_config(text)['diagnostics.log_verbosity'],
                                      parse_config(render_config(specification, log_verbosity=1))['diagnostics.log_verbosity']))
        self.assertEqual(Path('/tmp/syren-casting/airplay-' + IDENTITY + '.conf'), specification.config)

    def test_instance_command_confines_the_file_system_and_never_grants_the_state_volume(self):
        specification = CastingSpec(IDENTITY, 'office', 'SyrenSystem', 0)
        with patch('spotify_sessions.os.geteuid', return_value=100), \
                patch('spotify_sessions.resource.getrlimit', return_value=(95, 95)):
            command = instance_command(specification)
            moved = instance_command(CastingSpec(IDENTITY, 'office', 'SyrenSystem', 0, Path('/tmp/elsewhere')), 'shairport-sync')
        self.assertEqual(['/usr/bin/setpriv', '--pdeathsig', 'KILL', '--landlock-access', 'fs',
                          '--landlock-rule', 'path-beneath:execute,read-file,read-dir:/usr',
                          '--landlock-rule', 'path-beneath:read-file,read-dir:/etc',
                          '--landlock-rule', 'path-beneath:read-file,read-dir:/proc',
                          '--landlock-rule', 'path-beneath:read-file,read-dir:/sys',
                          '--landlock-rule', 'path-beneath:read-file,write-file,read-dir:/dev',
                          '--landlock-rule', 'path-beneath:read-file,write-file,read-dir,make-fifo:/tmp/syren-casting',
                          '--landlock-rule', 'path-beneath:read-dir:/run/syren-avahi-relay',
                          '--', 'chrt', '--fifo', str(SHAIRPORT_PRIORITY), '/usr/local/bin/shairport-sync',
                          '-c', str(specification.config)], command)
        self.assertEqual(84, SHAIRPORT_PRIORITY)
        for access, path in LANDLOCK_RULES:
            self.assertFalse('/var/lib/snapserver/'.startswith(path.rstrip('/') + '/') or path == '/', path)
        self.assertIn('path-beneath:read-file,write-file,read-dir,make-fifo:/tmp/elsewhere', moved)
        self.assertEqual(['shairport-sync', '-c', '/tmp/elsewhere/airplay-' + IDENTITY + '.conf'], moved[-3:])
        with patch('spotify_sessions.os.geteuid', return_value=100), \
                patch('spotify_sessions.resource.getrlimit', return_value=(0, 0)):
            self.assertEqual(['--', '/usr/local/bin/shairport-sync'], instance_command(specification)[-4:-2])

    def test_metadata_parser_handles_split_items_and_base64(self):
        stream = (b'noise' + item('svna', 'SyrenSystem · Smoke 0') + item('pbeg') +
                  b'<item><type>zz</type></item>\n' + item('PICT', bytes(range(256)), 'ssnc') + item('asar', 'Artist', 'core'))
        parser = MetadataParser()
        items = []
        for index in range(len(stream)):
            items += parser.feed(stream[index:index + 1])
        self.assertEqual([('ssnc', 'svna', 'SyrenSystem · Smoke 0'.encode()), ('ssnc', 'pbeg', None),
                          ('ssnc', 'PICT', bytes(range(256))), ('core', 'asar', b'Artist')], items)
        recorded = (b'<item><type>73736e63</type><code>73766e61</code><length>22</length>\n'
                    b'<data encoding="base64">\nU3lyZW5TeXN0ZW0gwrcgU21va2UgMA==</data></item>\n')
        self.assertEqual([('ssnc', 'svna', 'SyrenSystem · Smoke 0'.encode())], MetadataParser().feed(recorded))
        broken = b'<item><type>73736e63</type><code>' + item('pres')
        self.assertEqual([('ssnc', 'pres', None)], MetadataParser().feed(broken))
        small = MetadataParser(limit=64)
        self.assertEqual([], small.feed(b'<item><type>73736e63</type><code>70726573</code>' + b'A' * 100))
        self.assertEqual([('ssnc', 'pend', None)], small.feed(item('pend')))
        self.assertLess(len(small.buffer), 64)


class MapperTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.journal = CastingJournal(Path(temporary.name) / 'lifecycle.json')
        self.specification = CastingSpec(IDENTITY, 'office', 'SyrenSystem · Kantoor', 1)
        self.clock = Clock()
        self.logs = []
        self.mapper = CastingMapper(self.journal, self.specification, clock=self.clock, log=self.logs.append)
        self.adapter = AirPlayAdapter()
        self.parser = MetadataParser()

    def send(self, *codes):
        for code in codes:
            code, value = code if isinstance(code, tuple) else (code, None)
            for kind, item_code, data in self.parser.feed(item(code, value)):
                for event in self.adapter.feed(kind, item_code, data):
                    self.mapper.handle(event)

    def wait(self, seconds):
        self.clock.now += seconds
        self.mapper.tick()

    def level(self, status, seconds=1):
        self.clock.now += seconds
        self.mapper.handle(AdapterEvent('level', status))

    def events(self):
        return self.journal.events(1)

    def actions(self):
        return [event['action'] for event in self.events()]

    def playing(self, **identity):
        self.send(*connect(**identity), 'pbeg', ('styp', 'Buffered'), 'pres')

    def test_buffered_airplay2_sequence_starts_plays_pauses_and_ends(self):
        self.send(*connect())
        self.assertEqual(['start'], self.actions())
        start = self.events()[0]
        self.assertEqual({'generation': 1, 'sessionId': start['sessionId'], 'producerId': IDENTITY, 'eventSequence': 1,
                          'action': 'start', 'source': 'casting', 'protocol': 'airplay', 'ownerId': None,
                          'accountId': 'airplay:device:a1:b2:c3:d4:e5:f6', 'accountName': "Yme's iPhone",
                          'destination': 'office',
                          'transports': [{'id': 'airplay-' + IDENTITY, 'kind': 'snapcast', 'endpoint': 'airplay-' + IDENTITY,
                                          'available': True}]}, start)
        self.send('pbeg', ('styp', 'Buffered'), 'pres')
        self.assertEqual(['start', 'play'], self.actions())
        self.send('paus')
        self.wait(0.5)
        self.assertEqual(['start', 'play'], self.actions())
        self.wait(0.5)
        self.assertEqual(['start', 'play', 'pause'], self.actions())
        self.send('pres', 'pend', 'syen')
        self.assertEqual(['start', 'play', 'pause', 'play', 'interrupt'], self.actions())
        self.wait(4.5)
        self.assertEqual(5, len(self.actions()))
        self.wait(0.5)
        self.assertEqual(['start', 'play', 'pause', 'play', 'interrupt', 'end'], self.actions())
        events = self.events()
        self.assertEqual({start['sessionId']}, {event['sessionId'] for event in events})
        self.assertTrue(all('protocol' not in event and 'accountName' not in event for event in events[1:]))
        self.assertEqual([True, True, True, True, False, False], [event['transports'][0]['available'] for event in events])
        self.assertTrue(self.mapper.idle)

    def test_a_seek_does_not_pause(self):
        self.playing()
        self.send('paus', ('pfls', '1234'))
        self.wait(0.5)
        self.send('pres')
        self.wait(3)
        self.assertEqual(['start', 'play'], self.actions())

    def test_same_device_reconnect_keeps_session_and_claim(self):
        self.playing()
        session = self.events()[0]['sessionId']
        self.send('syen')
        self.wait(2)
        self.send(*connect(), 'pbeg', ('styp', 'Buffered'), 'pres')
        self.wait(10)
        self.assertEqual(['start', 'play', 'interrupt', 'transport'], self.actions())
        self.assertEqual({session}, {event['sessionId'] for event in self.events()})
        self.assertEqual(session, self.mapper.session)

    def test_another_device_takes_over_with_a_new_session(self):
        self.playing()
        first = self.events()[0]['sessionId']
        self.send('prmp', *connect(device='0F:0E:0D:0C:0B:0A', name='MacBook Air', address='192.0.2.20'))
        self.assertEqual(['start', 'play', 'interrupt', 'end', 'start'], self.actions())
        second = self.events()[-1]
        self.assertNotEqual(first, second['sessionId'])
        self.assertEqual(('airplay:device:0f:0e:0d:0c:0b:0a', 'MacBook Air'), (second['accountId'], second['accountName']))

    def test_classic_sender_uses_the_setup_dacp_id_and_flush_pause(self):
        self.send(('conn', '192.0.2.30'), ('clip', '192.0.2.30'), ('svip', '192.0.2.53'), ('snam', 'Old Mac'),
                  ('snua', 'AirPlay/377.40'), ('acre', '1234567'), ('daid', '0123456789abcdef'))
        self.assertEqual([], self.actions())
        self.send(('clip', '192.0.2.30'), ('svip', '192.0.2.53'))
        self.assertEqual(['start'], self.actions())
        self.assertEqual(('airplay:dacp:0123456789ABCDEF', 'Old Mac'), (self.events()[0]['accountId'], self.events()[0]['accountName']))
        self.send('pbeg', ('styp', 'Classic'))
        self.assertEqual(['start', 'play'], self.actions())
        self.send(('pfls', '88200'))
        self.wait(1.5)
        self.assertEqual(['start', 'play'], self.actions())
        self.wait(0.5)
        self.assertEqual(['start', 'play', 'pause'], self.actions())
        self.send('prsm')
        self.assertEqual(['start', 'play', 'pause', 'play'], self.actions())

    def test_a_dacp_id_from_get_info_is_ignored(self):
        self.send(('acre', '1'), ('daid', 'FEEDFACE'))
        self.send(('conn', '192.0.2.30'), ('clip', '192.0.2.30'), ('svip', '192.0.2.53'), ('snam', 'Picker'),
                  ('acre', '2'), ('daid', 'DEADBEEF'), ('snua', 'AirPlay/377.40'), ('acre', '3'),
                  ('clip', '192.0.2.30'), ('svip', '192.0.2.53'))
        self.assertEqual(['start'], self.actions())
        self.assertEqual('airplay:address:192.0.2.30', self.events()[0]['accountId'])
        self.assertNotIn('accountName', self.events()[0])

    def test_identity_items_after_resolution_are_ignored(self):
        self.send(*connect())
        self.send(('cdid', 'FF:FF:FF:FF:FF:FF'), ('snam', 'Someone else'), ('daid', 'ABCDEF'), ('clip', '192.0.2.99'),
                  ('svip', '192.0.2.53'), 'cmac')
        self.wait(2)
        self.assertEqual(['start'], self.actions())
        self.assertEqual('airplay:device:a1:b2:c3:d4:e5:f6', self.journal.state['receivers'][IDENTITY]['accountId'])

    def test_names_never_become_keys_and_address_keys_send_no_name(self):
        self.send(('conn', '192.0.2.40'), ('clip', '192.0.2.40'), ('svip', '192.0.2.53'), ('snam', "Yme's iPhone"))
        self.wait(0.5)
        self.assertEqual([], self.actions())
        self.wait(0.5)
        start = self.events()[0]
        self.assertEqual('airplay:address:192.0.2.40', start['accountId'])
        self.assertNotIn('accountName', start)
        self.assertNotIn('iPhone', start['accountId'])

    def test_idle_stream_while_playing_interrupts_without_ending(self):
        self.playing()
        for second in range(3):
            self.level('idle')
        self.assertEqual(['start', 'play'], self.actions())
        self.level('playing')
        for second in range(3):
            self.level('idle')
        self.assertEqual(['start', 'play'], self.actions())
        self.level('idle')
        self.assertEqual(['start', 'play', 'interrupt'], self.actions())
        self.level('idle', 30)
        self.assertEqual(['start', 'play', 'interrupt'], self.actions())
        self.assertIsNotNone(self.mapper.session)

    def test_the_stream_playing_again_restores_the_transport(self):
        self.playing()
        for second in range(4):
            self.level('idle')
        self.level('playing')
        self.assertEqual(['start', 'play', 'interrupt', 'transport'], self.actions())
        self.assertTrue(self.events()[-1]['transports'][0]['available'])
        self.send('paus')
        self.wait(1)
        for second in range(10):
            self.level('idle')
        self.assertEqual(['start', 'play', 'interrupt', 'transport', 'pause'], self.actions())

    def test_a_wrong_output_format_interrupts_until_a_matching_one_and_logs_once(self):
        self.send(*connect(), 'pbeg', ('styp', 'Buffered'), ('odsc', '44100/S32_LE/2'), 'pres')
        self.assertEqual(['start', 'interrupt', 'play', 'interrupt'], self.actions())
        self.send(('odsc', '44100/S32_LE/2'))
        self.level('playing')
        self.assertEqual(['start', 'interrupt', 'play', 'interrupt'], self.actions())
        self.assertEqual(1, len(self.logs))
        self.assertIn('44100/S32_LE/2', self.logs[0])
        self.send(('odsc', '48000/S16_LE/2'))
        self.assertEqual(['start', 'interrupt', 'play', 'interrupt', 'transport'], self.actions())
        self.assertTrue(self.mapper.available)

    def test_a_wrong_output_format_never_silences_the_next_sender(self):
        self.send(*connect(), 'pbeg', ('styp', 'Buffered'), ('odsc', '44100/S32_LE/2'), 'pres')
        self.assertFalse(self.mapper.available)
        # The next device never repeats the description, because its format was right from the start.
        self.send('prmp', *connect(device='0F:0E:0D:0C:0B:0A', name='MacBook Air', address='192.0.2.20'),
                  'pbeg', ('styp', 'Buffered'), 'pres')
        self.assertEqual(['start', 'play'], self.actions()[-2:])
        self.assertTrue(self.mapper.available)
        self.assertTrue(self.events()[-1]['transports'][0]['available'])

    def test_casting_heartbeats_repeat_the_latest_sequence(self):
        self.playing()
        self.assertIsNone(self.journal.heartbeat(self.specification, 1))
        session = self.events()[0]['sessionId']
        self.journal.acknowledge({'sessions': [{'id': session, 'eventSequence': 2, 'state': 'playing'}]})
        self.assertEqual({'sessionId': session, 'producerId': IDENTITY, 'eventSequence': 2, 'action': 'heartbeat',
                          'source': 'casting', 'generation': 1}, self.journal.heartbeat(self.specification, 1))
        self.send('paus')
        self.wait(1)
        self.assertIsNone(self.journal.heartbeat(self.specification, 1))
        self.journal.acknowledge({'sessions': [{'id': session, 'eventSequence': 3, 'state': 'paused'}]})
        self.assertEqual(3, self.journal.heartbeat(self.specification, 1)['eventSequence'])

    def stored(self):
        return self.journal.path.read_text()

    def test_the_journal_keeps_the_name_until_the_start_is_acknowledged_and_the_key_while_open(self):
        self.playing()
        session = self.events()[0]['sessionId']
        self.assertIn("Yme's iPhone", self.stored())
        self.journal.acknowledge({'sessions': [{'id': session, 'eventSequence': 2, 'state': 'playing'}]})
        self.assertNotIn('accountName', self.stored())
        self.assertNotIn('iPhone', self.stored())
        self.assertIn('airplay:device:a1:b2:c3:d4:e5:f6', self.stored())
        self.send('syen')
        self.wait(2)
        self.send(*connect(), 'pbeg', ('styp', 'Buffered'), 'pres')
        self.assertEqual(session, self.mapper.session)
        self.mapper.stop()
        self.assertEqual('end', self.actions()[-1])
        self.assertIsNone(json.loads(self.stored())['receivers'][IDENTITY]['accountId'])
        self.journal.acknowledge({'sessions': [{'id': session, 'eventSequence': self.events()[-1]['eventSequence'],
                                                'state': 'ended'}]})
        self.assertNotIn('a1:b2:c3:d4:e5:f6', self.stored())
        self.assertEqual({'receivers': {IDENTITY: {'sessionId': None, 'sequence': 5, 'accountId': None, 'commands': []}},
                          'pending': []}, json.loads(self.stored()))

    def test_an_abandoned_or_server_ended_session_keeps_no_device_key(self):
        key = 'airplay:device:a1:b2:c3:d4:e5:f6'
        for close in ('abandon', 'server'):
            self.journal.emit(self.specification, 'start', key, {'protocol': 'airplay', 'accountName': 'Phone'})
            session = self.mapper.session
            if close == 'abandon':
                self.assertTrue(self.journal.abandon(session, IDENTITY))
            else:
                self.journal.acknowledge({'sessions': [{'id': session, 'eventSequence': 1, 'state': 'ended'}]})
            self.assertNotIn('a1:b2:c3:d4:e5:f6', self.stored(), close)
            self.assertNotIn('Phone', self.stored(), close)

    def test_a_journal_from_before_drops_the_keys_of_closed_sessions_on_load(self):
        old = {'receivers': {IDENTITY: {'sessionId': None, 'sequence': 4, 'accountId': 'airplay:device:a1:b2:c3:d4:e5:f6',
                                        'commands': []},
                             'f' * 40: {'sessionId': 'open', 'sequence': 1, 'accountId': 'airplay:device:0f:0e:0d:0c:0b:0a',
                                        'commands': []}},
               'pending': []}
        self.journal.path.write_text(json.dumps(old))
        CastingJournal(self.journal.path)
        stored = json.loads(self.stored())
        self.assertIsNone(stored['receivers'][IDENTITY]['accountId'])
        self.assertEqual('airplay:device:0f:0e:0d:0c:0b:0a', stored['receivers']['f' * 40]['accountId'])

    def test_trace_formatter_never_prints_identity_values(self):
        formatter = TraceFormatter()
        private = {'conn': '192.0.2.10', 'clip': '192.0.2.10', 'svip': '192.0.2.53', 'snam': "Yme's iPhone",
                   'cdid': 'A1:B2:C3:D4:E5:F6', 'cmac': '02:11:22:33:44:55', 'cmod': 'iPhone17,1',
                   'daid': '0123456789ABCDEF', 'acre': '1234567'}
        lines = [formatter.format(2, 'ssnc', code, value.encode()) for code, value in private.items()]
        lines.append(formatter.format(2, 'core', 'asar', b'Private artist'))
        lines.append(formatter.format(2, 'ssnc', 'styp', b'Buffered'))
        lines.append(formatter.format(2, 'ssnc', 'pbeg', None))
        text = '\n'.join(lines)
        for value in list(private.values()) + ['Private artist']:
            self.assertNotIn(value, text)
        self.assertEqual(['airplay[2] conn 10 <ip 1>', 'airplay[2] clip 10 <ip 1>', 'airplay[2] svip 10 <ip 2>',
                          "airplay[2] snam 12 <name 1>", 'airplay[2] cdid 17 <cdid 1>'], lines[:5])
        self.assertEqual(['airplay[2] asar 14', 'airplay[2] styp 8 Buffered', 'airplay[2] pbeg 0'], lines[-3:])
        self.assertEqual('airplay[1] cdid 17 <cdid 1>', formatter.format(1, 'ssnc', 'cdid', b'A1:B2:C3:D4:E5:F6'))
        self.assertEqual('airplay[1] cdid 17 <cdid 2>', formatter.format(1, 'ssnc', 'cdid', b'F6:E5:D4:C3:B2:A1'))


if __name__ == '__main__':
    unittest.main()
