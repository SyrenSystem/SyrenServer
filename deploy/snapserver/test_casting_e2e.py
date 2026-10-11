import json
from pathlib import Path
import tempfile
import unittest

from casting_e2e import advertised, destination_of
from casting_supervisor import CastingSupervisor
from spotify_sessions import receiver_identity

INTERFACE = 'enx6c1ff770c66b'
PREFIX = 'Slaapkamer kinderen boven voorkant '
CONFIGURATION = {'stateId': 'home', 'groups': [
    {'id': 'office', 'name': 'Kantoor', 'enabledSources': ['casting'], 'speakerIds': ['pi-office']},
    {'id': 'study', 'name': 'kantoor', 'enabledSources': ['casting', 'spotify'], 'speakerIds': ['pi-study']},
    {'id': 'left', 'name': PREFIX + 'links', 'enabledSources': ['casting'], 'speakerIds': ['pi-left']},
    {'id': 'right', 'name': PREFIX + 'rechts', 'enabledSources': ['casting'], 'speakerIds': ['pi-right', 'pi-extra']},
    {'id': 'kitchen', 'name': 'Keuken', 'enabledSources': ['spotify'], 'speakerIds': ['pi-kitchen']},
]}
# Slots that do not follow the group order, as after rooms were added and removed.
SLOTS = {'house': 0, 'study': 1, 'right': 2, 'office': 3, 'left': 4}


def escape(text):
    # This writes every byte other than a letter, a digit, an underscore or a hyphen as three decimal digits, which unescape also reads.
    return ''.join(chr(byte) if chr(byte).isascii() and (chr(byte).isalnum() or chr(byte) in '_-') else f'\\{byte:03d}'
                   for byte in text.encode())


def line(interface, protocol, device_id, name, port, address='192.168.2.10'):
    return (f'=;{interface};{protocol};{escape(device_id + "@" + name)};_raop._tcp;local;syren-pc.local;{address};{port};'
            '"cn=0,1" "et=0,4" "tp=UDP" "am=Shairport Sync"')


class EndToEndTargetTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        directory = Path(temporary.name)
        slots = {'home': {receiver_identity('home', 'casting:airplay', destination): {'slot': slot, 'destination': destination,
                                                                                       'name': ''}
                          for destination, slot in SLOTS.items()}}
        (directory / 'state').mkdir(mode=0o700)
        (directory / 'state' / 'slots.json').write_text(json.dumps(slots))
        supervisor = CastingSupervisor(directory / 'state', directory / 'run', lambda *arguments: None)
        self.addCleanup(supervisor.close)
        supervisor.configuration = CONFIGURATION
        # The names, ports and device IDs the supervisor really gives its instances.
        self.specifications = sorted(supervisor.wanted().values(), key=lambda specification: specification.slot)
        lines = [line(INTERFACE, protocol, specification.device_id, specification.name, specification.port)
                 for specification in self.specifications for protocol in ('IPv6', 'IPv4')]
        lines.append(line('wlp0s20f3', 'IPv4', self.specifications[3].device_id, self.specifications[3].name, 7003,
                          '192.168.2.104'))
        # Another AirPlay device on the LAN may carry the same name, and it must never be picked.
        lines.insert(0, line(INTERFACE, 'IPv4', 'AABBCCDDEEFF', self.specifications[3].name, 7000, '192.168.2.50'))
        self.output = '\n'.join(['+;' + INTERFACE + ';IPv4;ignored;_raop._tcp;local'] + lines) + '\n'

    def test_cut_and_numbered_names_map_to_their_rooms(self):
        names = {specification.destination: specification.name for specification in self.specifications}
        self.assertEqual({'house': 'SyrenSystem', 'study': 'SyrenSystem · kantoor', 'office': 'SyrenSystem · Kantoor #2',
                          'right': 'SyrenSystem · Slaapkamer kinderen boven voorkant ',
                          'left': 'SyrenSystem · Slaapkamer kinderen boven voorka #2'}, names)
        services = advertised(self.output, INTERFACE)
        speakers = {'house': ['pi-office', 'pi-study', 'pi-left', 'pi-right', 'pi-extra'], 'study': ['pi-study'],
                    'office': ['pi-office'], 'left': ['pi-left'], 'right': ['pi-right', 'pi-extra']}
        for specification in self.specifications:
            destination, found_speakers, service = destination_of(CONFIGURATION, specification.name, services)
            self.assertEqual((specification.destination, speakers[specification.destination]),
                             (destination, found_speakers))
            self.assertEqual((specification.device_id, specification.port, '192.168.2.10'),
                             (service['device_id'], service['port'], service['address']))
            self.assertIn('am=Shairport Sync', service['txt'])

    def test_a_target_that_is_not_advertised_under_its_name_is_refused(self):
        office = next(specification for specification in self.specifications if specification.destination == 'office')
        busy = self.output.replace(escape(office.device_id + '@' + office.name), escape(office.device_id + '@SyrenSystem · Oud'))
        with self.assertRaisesRegex(AssertionError, 'belongs to destination office'):
            destination_of(CONFIGURATION, office.name, advertised(busy, INTERFACE))
        with self.assertRaisesRegex(AssertionError, 'No AirPlay target'):
            destination_of(CONFIGURATION, 'SyrenSystem · Keuken', advertised(self.output, INTERFACE))


if __name__ == '__main__':
    unittest.main()
