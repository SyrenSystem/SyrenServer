import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import time
import types
import unittest
from unittest.mock import patch

import casting_supervisor
from casting_sessions import SHAIRPORT_PRIORITY, instance_command, render_config
from casting_supervisor import CastingSupervisor, relay_check
from spotify_sessions import LifecycleJournal, ReceiverSpec, receiver_identity
from spotify_supervisor import SnapControl
from test_casting_sessions import Clock, config_value, connect, item, parse_config


class FakeProcess:
    def __init__(self, test, command, **options):
        self.test = test
        self.command = command
        self.options = options
        self.returncode = None
        self.stubborn = False
        self.pid = 40000 + len(test.processes)
        read_end, self.stderr_writer = os.pipe()
        self.stderr = os.fdopen(read_end, 'rb')
        test.addCleanup(os.close, self.stderr_writer)

    def poll(self):
        return self.returncode

    def terminate(self):
        self.test.signals.append(('TERM', self.pid))
        if not self.stubborn:
            self.returncode = -15

    def kill(self):
        self.test.signals.append(('KILL', self.pid))
        self.returncode = -9

    def wait(self, timeout=None):
        self.test.signals.append(('wait', self.pid))
        if self.returncode is None:
            raise subprocess.TimeoutExpired(self.command, timeout)
        return self.returncode


class FakeSnap(SnapControl):
    def __init__(self):
        super().__init__()
        self.calls = []
        self.status = {}
        self.on_add = None

    def request(self, method, parameters=None):
        self.calls.append((method, parameters))
        if method == 'Server.GetStatus':
            return {'server': {'streams': [{'id': identity, 'status': status} for identity, status in self.status.items()]}}
        if method == 'Stream.AddStream':
            if self.on_add:
                self.on_add(parameters)
            self.status[parameters['streamUri'].split('name=')[1].split('&')[0]] = 'idle'
        if method == 'Stream.RemoveStream':
            self.status.pop(parameters['id'], None)
        return {}

    def count(self, method):
        return sum(called == method for called, parameters in self.calls)


class FakeDelivery:
    def __init__(self, test):
        self.test = test

    def wait_for_publish(self, timeout=None):
        self.test.waits.append(timeout)

    def is_published(self):
        return self.test.online


class CastingSupervisorTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.shm = self.directory / 'shm'
        self.shm.mkdir()
        for slot in range(16):
            (self.shm / f'syren-airplay-{slot}').touch()
        self.clock = Clock()
        self.online = True
        self.published = []
        self.retained = []
        self.waits = []
        self.deferred = None
        self.logs = []
        self.signals = []
        self.processes = []
        self.on_spawn = None
        self.snap = FakeSnap()
        self.avahi_version = 'avahi 0.8'
        self.avahi_checks = 0
        self.nqptp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.addCleanup(self.nqptp.close)
        self.nqptp.bind(('127.0.0.1', 0))
        self.nqptp.setblocking(False)
        for target, value in (('casting_supervisor.subprocess.Popen', self.spawn), ('casting_supervisor.log', self.logs.append),
                              ('casting_supervisor.NQPTP_WAIT_SECONDS', 0.05)):
            patcher = patch(target, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        self.configuration = {'protocolVersion': 3, 'stateId': 'home', 'generation': 1, 'revision': 1, 'profiles': [],
                              'groups': [{'id': 'office', 'name': 'Kantoor', 'enabledSources': ['casting', 'spotify']}]}
        self.supervisor = self.make_supervisor()

    def make_supervisor(self):
        supervisor = CastingSupervisor(self.directory / 'state', self.directory / 'run', self.publish, snapcast=self.snap,
                                       connected=lambda: self.online, clock=self.clock, avahi_check=self.check,
                                       shm_directory=self.shm, nqptp_address=self.nqptp.getsockname(),
                                       background=self.run_in_background)
        self.addCleanup(supervisor.close)
        return supervisor

    def publish(self, topic, message, qos=1, retain=False):
        if retain:
            self.retained.append((topic, message, qos))
        else:
            self.published.append((topic, message, qos))
        self.signals.append(('publish', topic))
        return FakeDelivery(self)

    def run_in_background(self, function):
        # The check runs at once unless a test holds it back, like a slow relay.
        if self.deferred is None:
            function()
        else:
            self.deferred.append(function)

    def check(self):
        self.avahi_checks += 1
        return self.avahi_version

    def spawn(self, command, **options):
        process = FakeProcess(self, command, **options)
        if self.on_spawn:
            self.on_spawn(process)
        self.processes.append(process)
        return process

    def control_messages(self):
        messages = []
        while True:
            try:
                messages.append(self.nqptp.recv(1024))
            except BlockingIOError:
                return messages

    def configure(self, **changes):
        self.configuration = dict(self.configuration, **changes)
        self.supervisor.message('Configuration', self.configuration)
        self.supervisor.reconcile()

    def identity(self, destination):
        return receiver_identity('home', 'casting:airplay', destination)

    def target(self, destination='office'):
        return self.supervisor.targets[self.identity(destination)]

    def send(self, destination, *codes):
        target = self.target(destination)
        data = b''.join(item(*code) if isinstance(code, tuple) else item(code) for code in codes)
        self.supervisor.output.put((target['specification'].identity, target['token'], 'metadata', target['specification'].slot, data))
        self.deliver()

    def deliver(self):
        while not self.supervisor.output.empty():
            self.supervisor.received(*self.supervisor.output.get())

    def advance(self, seconds):
        self.clock.now += seconds
        for target in list(self.supervisor.targets.values()):
            target['mapper'].tick()

    def play(self, destination='office', **identity):
        self.send(destination, *connect(**identity), 'pbeg', ('styp', 'Buffered'), 'pres')
        return self.supervisor.journal.state['receivers'][self.identity(destination)]['sessionId']

    def actions(self, destination='office'):
        return [event['action'] for event in self.supervisor.journal.events(self.configuration['generation'])
                if event['producerId'] == self.identity(destination)]

    def running(self):
        return [process for process in self.processes if process.returncode is None]

    def test_instance_command_uses_pdeathsig_landlock_and_priority_84(self):
        with patch('spotify_sessions.os.geteuid', return_value=100), \
                patch('spotify_sessions.resource.getrlimit', return_value=(95, 95)):
            self.configure()
            specification = self.target()['specification']
            expected = instance_command(specification)
        process = self.target()['process']
        self.assertEqual(2, len(self.processes))
        self.assertEqual(expected, process.command)
        self.assertEqual(['/usr/bin/setpriv', '--pdeathsig', 'KILL', '--landlock-access', 'fs'], process.command[:5])
        separator = process.command.index('--')
        self.assertEqual(['chrt', '--fifo', str(SHAIRPORT_PRIORITY), '/usr/local/bin/shairport-sync', '-c',
                          str(specification.config)], process.command[separator + 1:])
        self.assertEqual(14, sum(argument.startswith('path-beneath:') or argument == '--landlock-rule'
                                 for argument in process.command[:separator]))
        self.assertEqual('unix:path=/run/syren-avahi-relay/system_bus_socket', process.options['env']['DBUS_SYSTEM_BUS_ADDRESS'])
        self.assertEqual((subprocess.DEVNULL, subprocess.DEVNULL, subprocess.PIPE),
                         (process.options['stdin'], process.options['stdout'], process.options['stderr']))
        self.assertEqual(render_config(specification), specification.config.read_text())
        self.assertEqual(0o600, specification.config.stat().st_mode & 0o777)
        self.assertEqual(0o700, (self.directory / 'run').stat().st_mode & 0o777)
        self.assertEqual({b'/syren-airplay-0 T\0', b'/syren-airplay-1 T\0'}, set(self.control_messages()))

    def test_the_audio_fifo_is_held_open_before_the_stream_and_the_instance(self):
        order = []

        def writable(path):
            # Without a reader, a non blocking open for writing fails with ENXIO.
            descriptor = os.open(path, os.O_WRONLY | os.O_NONBLOCK)
            os.close(descriptor)

        def added(parameters):
            writable(parameters['streamUri'].removeprefix('pipe://').split('?')[0])
            order.append('stream')

        def spawned(process):
            specification = next(target['specification'] for target in self.supervisor.targets.values()
                                 if str(target['specification'].config) == process.command[-1])
            self.assertIn(specification.stream, self.snap.status)
            writable(specification.fifo)
            writable(specification.meta)
            order.append('instance')

        self.snap.on_add, self.on_spawn = added, spawned
        self.configure()
        self.assertEqual(['stream', 'instance', 'stream', 'instance'], order)
        specification = self.target()['specification']
        self.assertIn(('Stream.AddStream', {'streamUri': f'pipe://{specification.fifo}?name={specification.stream}'
                                                         '&sampleformat=48000:16:2&codec=pcm'}), self.snap.calls)
        inode = specification.fifo.stat().st_ino
        self.target()['process'].returncode = 1
        self.supervisor.reconcile()
        writable(specification.fifo)
        self.assertEqual(inode, specification.fifo.stat().st_ino)

    def test_supervisor_restart_reuses_the_audio_fifo_and_stream(self):
        self.configure()
        specification = self.target()['specification']
        inode = specification.fifo.stat().st_ino
        stale = self.directory / 'run' / ('airplay-' + 'f' * 40 + '.fifo')
        os.mkfifo(stale, 0o600)
        self.supervisor.close()
        self.assertTrue(specification.config.exists() and specification.meta.exists())
        self.snap.calls.clear()
        self.supervisor = self.make_supervisor()
        self.assertFalse(specification.config.exists() or specification.meta.exists())
        self.configure()
        self.assertEqual(inode, specification.fifo.stat().st_ino)
        self.assertFalse(stale.exists())
        self.assertEqual(0, self.snap.count('Stream.AddStream'))
        self.assertEqual(0, self.snap.count('Stream.RemoveStream'))
        self.assertTrue(specification.config.exists())

    def rendered_names(self):
        return [parse_config(target['specification'].config.read_text())['general.name']
                for target in self.supervisor.targets.values()]

    def test_equal_group_names_get_distinct_airplay_names(self):
        prefix = 'Slaapkamer kinderen boven voorkant '
        cases = [('Kantoor', 'Kantoor'), (prefix + 'links', prefix + 'rechts'), ('Kantoor', 'kantoor'), ('Hal\n', 'Hal ')]
        for revision, (first, second) in enumerate(cases, start=2):
            groups = [{'id': 'office', 'name': first, 'enabledSources': ['casting']},
                      {'id': 'study', 'name': second, 'enabledSources': ['casting']}]
            self.configure(revision=revision, groups=groups)
            names = [config_value(name) for name in self.rendered_names()]
            self.assertEqual(3, len(names), (first, second))
            self.assertEqual(3, len({name.lower() for name in names}), names)
            self.assertTrue(all(len(name.encode()) <= 50 for name in names), names)
        office, study = self.target('office')['specification'], self.target('study')['specification']
        self.assertLess(office.slot, study.slot)
        self.assertEqual(('SyrenSystem · Hal ', 'SyrenSystem · Hal #2'), (office.name, study.name))

    def running_names(self):
        # The name each running instance advertises, read from the config it started with.
        return sorted(config_value(parse_config(target['specification'].config.read_text())['general.name'])
                      for target in self.supervisor.targets.values() if target['process'] is not None)

    def rooms(self, revision, *names):
        groups = [{'id': destination, 'name': name, 'enabledSources': ['casting']}
                  for destination, name in zip(('office', 'study', 'kitchen'), names)]
        self.configure(revision=revision, groups=groups)

    def watch_names(self):
        # Avahi refuses a name that another running instance advertises, so a start with such a name is a clash.
        clashes = []

        def spawned(process):
            name = config_value(parse_config(Path(process.command[-1]).read_text())['general.name'])
            if name.lower() in [running.lower() for running in self.running_names()]:
                clashes.append(name)
        self.on_spawn = spawned
        return clashes

    def test_a_name_held_by_a_busy_instance_is_never_given_to_another(self):
        clashes = self.watch_names()
        self.rooms(2, 'Kantoor', 'Studie')
        self.play('office')
        office, study = self.target('office')['process'], self.target('study')['process']
        self.rooms(3, 'Bar', 'Kantoor')
        for second in range(3):
            self.clock.now += 1
            self.supervisor.reconcile()
        self.assertEqual(['SyrenSystem', 'SyrenSystem · Kantoor', 'SyrenSystem · Studie'], self.running_names())
        self.assertEqual((office, study), (self.target('office')['process'], self.target('study')['process']))
        self.assertEqual((None, None), (office.returncode, study.returncode))
        self.assertEqual(1, sum('keeps its old name, because instance' in line for line in self.logs))
        self.send('office', 'pend', 'syen')
        self.advance(5)
        self.supervisor.reconcile()
        self.assertEqual((-15, -15), (office.returncode, study.returncode))
        self.assertEqual(['SyrenSystem', 'SyrenSystem · Bar', 'SyrenSystem · Kantoor'], self.running_names())
        self.assertEqual([], clashes)

    def test_a_new_room_waits_for_a_name_a_busy_instance_still_advertises(self):
        clashes = self.watch_names()
        self.configure()
        self.play()
        office = self.target()['process']
        self.rooms(2, 'Bar', 'Kantoor')
        for second in range(3):
            self.clock.now += 1
            self.supervisor.reconcile()
        self.assertIsNone(self.target('study')['process'])
        self.assertIs(office, self.target()['process'])
        self.assertEqual(['SyrenSystem', 'SyrenSystem · Kantoor'], self.running_names())
        self.assertEqual(1, sum('waits, because instance' in line for line in self.logs))
        self.send('office', 'pend', 'syen')
        self.advance(5)
        self.supervisor.reconcile()
        self.assertEqual(['SyrenSystem', 'SyrenSystem · Bar', 'SyrenSystem · Kantoor'], self.running_names())
        self.assertEqual([], clashes)

    def test_idle_rooms_swap_names_in_one_pass(self):
        clashes = self.watch_names()
        self.rooms(2, 'Kantoor', 'Studie')
        office, study = self.target('office')['process'], self.target('study')['process']
        self.rooms(3, 'Studie', 'Kantoor')
        self.assertEqual((-15, -15), (office.returncode, study.returncode))
        self.assertEqual(('SyrenSystem · Studie', 'SyrenSystem · Kantoor'),
                         (self.target('office')['specification'].name, self.target('study')['specification'].name))
        self.assertEqual(['SyrenSystem', 'SyrenSystem · Kantoor', 'SyrenSystem · Studie'], self.running_names())
        self.assertEqual(5, len(self.processes))
        self.assertEqual([], clashes)

    def test_a_busy_swap_finishes_once_the_busy_room_is_idle(self):
        clashes = self.watch_names()
        self.rooms(2, 'Kantoor', 'Studie')
        self.play('office')
        office, study = self.target('office')['process'], self.target('study')['process']
        self.rooms(3, 'Studie', 'Kantoor')
        for second in range(3):
            self.clock.now += 1
            self.supervisor.reconcile()
        self.assertEqual((None, None), (office.returncode, study.returncode))
        self.assertEqual(['SyrenSystem', 'SyrenSystem · Kantoor', 'SyrenSystem · Studie'], self.running_names())
        self.send('office', 'pend', 'syen')
        self.advance(5)
        self.supervisor.reconcile()
        self.assertEqual((-15, -15), (office.returncode, study.returncode))
        self.assertEqual(('SyrenSystem · Studie', 'SyrenSystem · Kantoor'),
                         (self.target('office')['specification'].name, self.target('study')['specification'].name))
        self.assertEqual(['SyrenSystem', 'SyrenSystem · Kantoor', 'SyrenSystem · Studie'], self.running_names())
        self.assertEqual([], clashes)

    def test_rename_waits_until_idle(self):
        self.configure()
        first = self.target()['process']
        self.play()
        self.configure(revision=2, groups=[{'id': 'office', 'name': 'Werkkamer', 'enabledSources': ['casting']}])
        self.supervisor.reconcile()
        self.assertIs(first, self.target()['process'])
        self.assertIn('SyrenSystem · Kantoor', self.target()['specification'].config.read_text())
        self.send('office', 'pend', 'syen')
        self.advance(5)
        self.supervisor.reconcile()
        self.assertEqual(-15, first.returncode)
        self.assertIsNot(first, self.target()['process'])
        self.assertIn('name = "SyrenSystem · Werkkamer";', self.target()['specification'].config.read_text())
        self.assertEqual(['start', 'play', 'interrupt', 'end'], self.actions())
        self.assertEqual(self.target()['specification'].slot, self.supervisor.slots['home'][self.identity('office')]['slot'])
        self.assertEqual('SyrenSystem · Werkkamer', self.supervisor.slots['home'][self.identity('office')]['name'])

    def test_removed_destination_ends_session_and_removes_stream_and_fifos(self):
        kitchen = {'id': 'kitchen', 'name': 'Keuken', 'enabledSources': ['casting']}
        self.configure(groups=self.configuration['groups'] + [kitchen])
        target = self.target()
        specification, process = target['specification'], target['process']
        self.play()
        self.configure(revision=2, groups=[{'id': 'office', 'name': 'Kantoor', 'enabledSources': ['spotify']}, kitchen])
        self.assertEqual(['start', 'play', 'end'], self.actions())
        self.assertFalse(self.supervisor.journal.events(1)[-1]['transports'][0]['available'])
        self.assertIn(('Stream.RemoveStream', {'id': specification.stream}), self.snap.calls)
        self.assertEqual(-15, process.returncode)
        for path in (specification.fifo, specification.meta, specification.config):
            self.assertFalse(path.exists(), path)
        self.assertNotIn(specification.identity, self.supervisor.targets)
        self.assertNotIn(specification.identity, self.supervisor.slots['home'])
        self.assertEqual({self.identity('house'), self.identity('kitchen')}, set(self.supervisor.targets))
        self.supervisor.journal.acknowledge({'sessions': [{'id': self.supervisor.journal.events(1)[0]['sessionId'],
                                                           'state': 'ended', 'eventSequence': 3}]})
        self.supervisor.reconcile()
        self.assertNotIn(specification.identity, self.supervisor.journal.state['receivers'])

    def test_a_dead_instance_ends_its_session_and_backs_off(self):
        self.configure()
        self.play()
        delays = []
        for attempt in range(7):
            before = len(self.processes)
            self.target()['process'].returncode = 1
            self.supervisor.reconcile()
            waited = 0
            while len(self.processes) == before:
                self.clock.now += 0.5
                waited += 0.5
                self.supervisor.reconcile()
            delays.append(waited)
        self.assertEqual([1, 2, 4, 8, 16, 30, 30], delays)
        self.assertEqual(['start', 'play', 'end'], self.actions())
        self.assertEqual(1, sum('exited with 1' in line for line in self.logs))
        self.clock.now += 60
        self.target()['process'].returncode = 2
        self.supervisor.reconcile()
        self.clock.now += 1
        self.supervisor.reconcile()
        office = [process for process in self.processes if process.command[-1] == str(self.target()['specification'].config)]
        self.assertEqual(9, len(office))
        self.assertEqual(1, sum('exited with 2' in line for line in self.logs))

    def test_lost_avahi_restarts_the_instance_when_idle(self):
        self.configure()
        target = self.target()
        first = target['process']
        slot = target['specification'].slot
        self.play()
        checks = self.avahi_checks
        self.supervisor.output.put((target['specification'].identity, target['token'], 'stderr', slot,
                                    b'warning: an ordinary line\nsecond line'))
        self.deliver()
        self.assertIn(f'airplay[{slot}] warning: an ordinary line', self.logs)
        self.assertNotIn(f'airplay[{slot}] second line', self.logs)
        self.send('office', 'syav')
        self.supervisor.reconcile()
        self.assertTrue(target['unadvertised'])
        self.assertIs(first, target['process'])
        self.assertEqual(checks, self.avahi_checks)
        self.send('office', 'syen')
        self.advance(5)
        self.avahi_version = None
        self.supervisor.reconcile()
        self.assertIs(first, target['process'])
        self.assertEqual(checks + 1, self.avahi_checks)
        self.avahi_version = 'avahi 0.8'
        self.clock.now += 5
        self.supervisor.reconcile()
        self.assertEqual(-15, first.returncode)
        self.assertIsNot(first, target['process'])
        self.assertFalse(target['unadvertised'])
        self.assertEqual(1, sum('lost Avahi' in line for line in self.logs))

    def test_avahi_text_on_stderr_never_restarts_the_instance(self):
        self.configure()
        target = self.target()
        first = target['process']
        slot = target['specification'].slot
        checks = self.avahi_checks
        # The first two lines are the exact bytes a LAN sender made the real shairport-sync write with a SETUP timingProtocol.
        forged = (b'warning: Shairport Sync can not handle streams of this type: "x\nwarning: avahi: client failure: forged".\n'
                  b'warning:     avahi: client failure: x\n'
                  b'warning: avahi: client failure: Daemon not running\n'
                  b'warning: avahi: failed to create a replacement client object: Daemon not running\n')
        self.supervisor.output.put((target['specification'].identity, target['token'], 'stderr', slot, forged))
        self.deliver()
        # A sender chooses the codes of core items, so only an ssnc item from the current instance counts.
        self.send('office', ('syav', None, 'core'))
        self.supervisor.output.put((target['specification'].identity, target['token'] - 1, 'metadata', slot, item('syav')))
        self.deliver()
        for second in range(3):
            self.clock.now += 5
            self.supervisor.reconcile()
        self.assertIs(first, target['process'])
        self.assertIsNone(first.returncode)
        self.assertFalse(target['unadvertised'])
        self.assertEqual(checks, self.avahi_checks)
        self.assertEqual(0, sum('lost Avahi' in line for line in self.logs))
        self.assertIn(f'airplay[{slot}] warning: avahi: client failure: forged".', self.logs)
        self.assertIn(f'airplay[{slot}] warning: avahi: client failure: Daemon not running', self.logs)
        self.send('office', 'syav')
        self.supervisor.reconcile()
        self.assertEqual(-15, first.returncode)
        self.assertIsNot(first, target['process'])
        self.assertEqual(1, sum('lost Avahi' in line for line in self.logs))

    def test_changed_nqptp_region_restarts_the_instance(self):
        self.configure()
        target = self.target()
        first = target['process']
        self.play()
        region = self.shm / 'syren-airplay-1'
        replacement = self.shm / 'replacement'
        replacement.touch()
        replacement.replace(region)
        self.supervisor.reconcile()
        self.assertEqual(-15, first.returncode)
        self.assertEqual(['start', 'play', 'end'], self.actions())
        self.assertEqual(region.stat().st_ino, target['inode'])
        second = target['process']
        region.unlink()
        self.supervisor.reconcile()
        self.assertEqual(-15, second.returncode)
        self.assertIsNone(target['process'])
        # nqptp gets 0.05 s here, so the next pass gives up and asks again 10 s later.
        self.clock.now += 1
        self.supervisor.reconcile()
        region.touch()
        self.clock.now += 9
        self.supervisor.reconcile()
        self.assertIsNone(target['process'])
        self.clock.now += 1
        self.supervisor.reconcile()
        self.assertIsNotNone(target['process'])

    def test_missing_nqptp_holds_instances_back_without_waiting_or_restart_loops(self):
        for region in self.shm.iterdir():
            region.unlink()
        with patch('casting_supervisor.NQPTP_WAIT_SECONDS', 1.0):
            started = time.monotonic()
            self.configure()
            # The real wait for nqptp is 1 s, and the loop never spends it.
            self.assertLess(time.monotonic() - started, 0.5)
            self.assertEqual([], self.processes)
            self.assertEqual({b'/syren-airplay-0 T\0', b'/syren-airplay-1 T\0'}, set(self.control_messages()))
            for second in range(10):
                self.clock.now += 1
                self.supervisor.reconcile()
            self.assertEqual([], self.processes)
            self.assertEqual([], self.control_messages())
            self.assertEqual(1, sum('nqptp' in line for line in self.logs))
            self.clock.now += 1
            (self.shm / 'syren-airplay-0').touch()
            (self.shm / 'syren-airplay-1').touch()
            self.supervisor.reconcile()
        self.assertEqual(2, len(self.running()))
        self.assertEqual({b'/syren-airplay-0 T\0', b'/syren-airplay-1 T\0'}, set(self.control_messages()))

    def test_a_region_that_appears_late_starts_the_instance_on_a_later_pass(self):
        region = self.shm / 'syren-airplay-1'
        region.unlink()
        with patch('casting_supervisor.NQPTP_WAIT_SECONDS', 1.0):
            self.configure()
            self.assertIsNone(self.target()['process'])
            region.touch()
            self.clock.now += 0.5
            self.supervisor.reconcile()
        self.assertIsNotNone(self.target()['process'])
        self.assertEqual(0, sum('nqptp' in line for line in self.logs))

    def test_the_dbus_check_never_runs_on_the_loop(self):
        self.deferred = []
        self.configure()
        self.assertEqual(([], 0, 1), (self.processes, self.avahi_checks, len(self.deferred)))
        self.clock.now += 1
        self.supervisor.reconcile()
        # One check at a time, however long the relay takes.
        self.assertEqual(([], 0, 1), (self.processes, self.avahi_checks, len(self.deferred)))
        self.deferred.pop()()
        self.assertEqual(1, self.avahi_checks)
        self.supervisor.reconcile()
        self.assertEqual(2, len(self.running()))

    def test_failing_dbus_check_starts_no_instance(self):
        self.avahi_version = None
        self.configure()
        self.assertEqual(([], 1), (self.processes, self.avahi_checks))
        for second in range(29):
            self.clock.now += 1
            self.supervisor.reconcile()
        self.assertEqual(([], 1, []), (self.processes, self.avahi_checks, self.control_messages()))
        self.clock.now += 1
        self.supervisor.reconcile()
        self.assertEqual(2, self.avahi_checks)
        self.avahi_version = 'avahi 0.8'
        self.clock.now += 30
        self.supervisor.reconcile()
        self.assertEqual((2, 3), (len(self.running()), self.avahi_checks))
        self.assertEqual(1, sum('D-Bus check failed' in line for line in self.logs))

    def test_the_default_dbus_check_asks_the_relay(self):
        relay = types.ModuleType('casting_dbus_relay')
        calls = []
        relay.check_avahi = lambda socket_path, timeout=2.0: calls.append(socket_path) or 'avahi 0.8'
        with patch.dict(sys.modules, {'casting_dbus_relay': relay}):
            self.assertEqual('avahi 0.8', relay_check())
        self.assertEqual(['/run/syren-avahi-relay/system_bus_socket'], calls)

    def test_server_ended_session_drops_the_sender(self):
        self.configure()
        first = self.target()['process']
        session = self.play()
        self.supervisor.message('Catalogue', {'generation': 1, 'revision': 1, 'sessions': [
            {'id': session, 'state': 'ended', 'eventSequence': 2}]})
        self.assertEqual(-15, first.returncode)
        self.assertIsNone(self.supervisor.journal.state['receivers'][self.identity('office')]['sessionId'])
        self.assertEqual([], self.actions())
        self.supervisor.reconcile()
        self.assertIsNotNone(self.target()['process'])
        self.assertIsNot(first, self.target()['process'])

    def acknowledge(self, session):
        # The first catalogue of a generation brings a reconcile, which the second one acknowledges.
        for revision in (1, 2):
            self.supervisor.message('Catalogue', {'generation': 1, 'revision': revision, 'sessions': [
                {'id': session, 'state': 'playing', 'eventSequence': revision + 1}]})
        self.assertEqual([], self.supervisor.journal.state['pending'])

    def lifecycle(self):
        return [(message, qos) for topic, message, qos in self.published if topic == 'Lifecycle']

    def test_the_sender_volume_rides_on_heartbeats_and_goes_out_at_once_on_change(self):
        self.configure()
        session = self.play()
        journal = (self.directory / 'state' / 'lifecycle.json').read_text()
        # The start and play events are not acknowledged yet, so a heartbeat would outrun them.
        self.send('office', ('pvol', '-24.00,0.00,0.00,0.00'))
        self.assertEqual([], self.lifecycle())
        self.assertEqual(journal, (self.directory / 'state' / 'lifecycle.json').read_text())
        self.acknowledge(session)
        heartbeat = {'sessionId': session, 'producerId': self.identity('office'), 'eventSequence': 3, 'action': 'heartbeat',
                     'source': 'casting', 'generation': 1, 'volume': 0.019498}
        self.assertEqual([(heartbeat, 0)], self.lifecycle())
        self.published.clear()
        self.supervisor.prepare_heartbeats()
        self.supervisor.publish_heartbeats()
        self.assertEqual([(heartbeat, 0)], self.lifecycle())
        self.published.clear()
        self.send('office', ('pvol', '-144.00,0.00,0.00,0.00'))
        self.assertEqual([(dict(heartbeat, volume=0.000001), 0)], self.lifecycle())
        # The same volume again, or one that does not parse, sends nothing extra.
        self.send('office', ('pvol', '-144.00,0.00,0.00,0.00'), ('pvol', 'loud'))
        self.assertEqual(1, len(self.lifecycle()))
        self.online = False
        self.send('office', ('pvol', '0.00,0.00,0.00,0.00'))
        self.assertEqual(1, len(self.lifecycle()))
        self.online = True
        self.supervisor.prepare_heartbeats()
        self.supervisor.publish_heartbeats()
        self.assertEqual((dict(heartbeat, volume=1.0), 0), self.lifecycle()[-1])
        # The volume never reaches the journal, so it never takes an event number.
        self.assertNotIn('volume', (self.directory / 'state' / 'lifecycle.json').read_text())
        self.assertEqual([], self.supervisor.journal.state['pending'])

    def test_a_volume_held_back_by_unsent_events_goes_out_with_the_acknowledgement(self):
        self.configure()
        session = self.play()
        self.send('office', ('pvol', '-24.00,0.00,0.00,0.00'))
        self.assertEqual([], self.lifecycle())
        # Only the catalogue arrives, so no loop step and no heartbeat tick sends it.
        self.acknowledge(session)
        self.assertEqual([0.019498], [message.get('volume') for message, qos in self.lifecycle()])
        self.assertEqual(('heartbeat', 0), (self.lifecycle()[0][0]['action'], self.lifecycle()[0][1]))
        # A later catalogue that changes nothing for the session sends no extra heartbeat.
        self.supervisor.message('Catalogue', {'generation': 1, 'revision': 3, 'sessions': [
            {'id': session, 'state': 'playing', 'eventSequence': 3}]})
        self.assertEqual(1, len(self.lifecycle()))

    def test_a_heartbeat_tick_after_a_volume_change_never_sends_the_old_volume(self):
        self.configure()
        session = self.play()
        self.send('office', ('pvol', '-24.00,0.00,0.00,0.00'))
        self.acknowledge(session)
        self.supervisor.prepare_heartbeats()
        self.published.clear()
        self.send('office', ('pvol', '0.00,0.00,0.00,0.00'))
        # The heartbeat thread ticks before the loop gets to prepare the heartbeats again.
        self.supervisor.publish_heartbeats()
        self.assertEqual([1.0, 1.0], [message['volume'] for message, qos in self.lifecycle()])

    def test_the_heartbeat_thread_waits_while_a_volume_goes_out(self):
        self.configure()
        session = self.play()
        self.send('office', ('pvol', '-24.00,0.00,0.00,0.00'))
        self.acknowledge(session)
        self.supervisor.prepare_heartbeats()
        self.published.clear()
        stale = self.supervisor.heartbeats
        ticked = []
        original = self.publish

        def publish(topic, message, qos=1, retain=False):
            # The tick runs while the new volume is being sent, as the thread would.
            if not ticked:
                ticked.append(threading.Thread(target=self.supervisor.publish_heartbeats))
                ticked[0].start()
                ticked[0].join(0.2)
            return original(topic, message, qos, retain)

        self.supervisor.publish = publish
        self.send('office', ('pvol', '0.00,0.00,0.00,0.00'))
        ticked[0].join(2)
        self.assertIsNot(stale, self.supervisor.heartbeats)
        self.assertEqual([1.0, 1.0], [message['volume'] for message, qos in self.lifecycle()])

    def test_a_new_sender_heartbeat_has_no_volume_until_it_sends_one(self):
        self.configure()
        session = self.play()
        self.send('office', ('pvol', '-15.00,0.00,0.00,0.00'))
        self.acknowledge(session)
        self.send('office', 'syen', ('conn', '192.0.2.10'))
        self.assertEqual(['interrupt'], [event['action'] for event in self.supervisor.journal.state['pending']])
        self.supervisor.message('Catalogue', {'generation': 1, 'revision': 3, 'sessions': [
            {'id': session, 'state': 'playing', 'eventSequence': 4}]})
        self.supervisor.prepare_heartbeats()
        self.assertEqual([session], [event['sessionId'] for process, event in self.supervisor.heartbeats])
        self.assertNotIn('volume', self.supervisor.heartbeats[0][1])

    def catalogue(self, generation, revision, *sessions):
        return {'generation': generation, 'revision': revision,
                'sessions': [{'id': session, 'state': 'playing', 'eventSequence': 0} for session in sessions]}

    def new_generation(self, *sessions):
        self.configure(generation=2, revision=2)
        self.supervisor.message('Catalogue', self.catalogue(2, 1, *sessions))
        return {destination: self.actions(destination)[-1:] for destination in ('house', 'office', 'kitchen')}

    def test_new_generation_reconciles_only_available_sessions(self):
        self.configure(groups=self.configuration['groups'] + [{'id': 'kitchen', 'name': 'Keuken', 'enabledSources': ['casting']}])
        playing = self.play('house')
        stalled = self.play('office', device='0A:00:00:00:00:01')
        self.send('office', 'pend')
        self.send('kitchen', *connect(device='0A:00:00:00:00:02'))
        connected = self.supervisor.journal.state['receivers'][self.identity('kitchen')]['sessionId']
        last = self.new_generation(playing, stalled, connected)
        self.assertEqual({'house': ['reconcile'], 'office': ['interrupt'], 'kitchen': ['start']}, last)
        self.supervisor.message('Catalogue', self.catalogue(2, 2, playing, stalled, connected))
        self.assertEqual(1, self.actions('house').count('reconcile'))
        self.send('office', 'pbeg')
        self.assertEqual(['transport'], self.actions('office')[-1:])

    def test_new_state_with_a_lower_generation_still_reconciles(self):
        self.configure(generation=5, revision=1)
        self.supervisor.message('Catalogue', self.catalogue(5, 1))
        self.configure(stateId='rebuilt', generation=1, revision=1)
        self.identity = lambda destination: receiver_identity('rebuilt', 'casting:airplay', destination)
        session = self.play()
        self.supervisor.message('Catalogue', self.catalogue(1, 1, session))
        before = self.actions().count('reconcile')
        # The server restarts, so generation 2 of the new state marks every session interrupted.
        self.configure(stateId='rebuilt', generation=2, revision=2)
        self.supervisor.message('Catalogue', self.catalogue(2, 1, session))
        self.assertEqual(['start', 'play', 'reconcile'], self.actions()[:2] + self.actions()[-1:])
        self.assertEqual(before + 1, self.actions().count('reconcile'))

    def test_new_generation_does_not_revive_a_released_session(self):
        self.configure()
        session = self.play()
        self.send('office', 'syen')
        last = self.new_generation(session)
        self.assertEqual(['interrupt'], last['office'])
        self.advance(5)
        self.assertEqual(['start', 'play', 'interrupt', 'end'], self.actions())

    def test_startup_ends_sessions_left_in_the_journal(self):
        self.configure()
        session = self.play()
        orphan = self.supervisor.journal.receiver('unknown')
        orphan['sessionId'] = 'lost'
        self.supervisor.journal.save()
        self.supervisor.close()
        self.supervisor = self.make_supervisor()
        events = self.supervisor.journal.events(1)
        self.assertEqual(['start', 'play', 'end'], [event['action'] for event in events if event['sessionId'] == session])
        end = events[-1]
        self.assertEqual(('office', 'airplay-' + self.identity('office'), False),
                         (end['destination'], end['transports'][0]['endpoint'], end['transports'][0]['available']))
        self.assertIsNone(self.supervisor.journal.state['receivers']['unknown']['sessionId'])
        self.assertFalse(any(event['sessionId'] == 'lost' for event in events))
        self.configure()
        self.supervisor.publish_pending()
        self.assertIn(('Lifecycle', dict(end, generation=1), 1), self.published)

    def test_rejected_session_is_abandoned_and_sender_dropped(self):
        self.configure()
        first = self.target()['process']
        session = self.play()
        self.supervisor.message('LifecycleRejected', {'generation': 1, 'sessionId': session, 'producerId': self.identity('house')})
        self.assertIsNone(first.returncode)
        self.supervisor.message('LifecycleRejected', {'generation': 1, 'sessionId': session, 'producerId': self.identity('office')})
        self.assertEqual(-15, first.returncode)
        self.assertEqual([], self.actions())
        self.assertIsNone(self.supervisor.journal.state['receivers'][self.identity('office')]['sessionId'])
        self.supervisor.reconcile()
        second = self.target()['process']
        self.supervisor.message('LifecycleRejected', {'generation': 1, 'sessionId': session, 'producerId': self.identity('office')})
        self.assertIsNone(second.returncode)
        self.assertEqual(1, sum('rejected' in line for line in self.logs))

    def test_casting_and_spotify_sessions_in_one_catalogue_never_touch_each_other_journal(self):
        spotify = LifecycleJournal(self.directory / 'spotify.json')
        receiver = ReceiverSpec('receiver', 'person', 'account', 'house', 'SyrenSystem')
        spotify.handle(receiver, {'event': 'syren_intent', 'action': 'play', 'commandId': 'one', 'accountId': 'account'})
        spotify_session = spotify.receiver('receiver')['sessionId']
        self.configure()
        self.supervisor.message('Catalogue', {'generation': 1, 'revision': 0, 'sessions': []})
        casting_session = self.play()
        catalogue = {'generation': 1, 'revision': 1, 'sessions': [
            {'id': spotify_session, 'state': 'ended', 'eventSequence': 2},
            {'id': casting_session, 'state': 'connected', 'eventSequence': 1}]}
        self.supervisor.message('Catalogue', catalogue)
        spotify.acknowledge(catalogue)
        self.assertEqual(casting_session, self.supervisor.journal.state['receivers'][self.identity('office')]['sessionId'])
        self.assertEqual(['play'], self.actions())
        self.assertIsNone(spotify.receiver('receiver')['sessionId'])
        self.assertNotIn('receiver', self.supervisor.journal.state['receivers'])
        spotify.handle(receiver, {'event': 'syren_intent', 'action': 'play', 'commandId': 'two', 'accountId': 'account'})
        second = {'generation': 1, 'revision': 2, 'sessions': [
            {'id': casting_session, 'state': 'ended', 'eventSequence': 2}]}
        spotify.acknowledge(second)
        self.assertEqual(['start', 'play'], [event['action'] for event in spotify.events(1)])
        self.assertEqual({'spotify'}, {event['source'] for event in spotify.events(1)})
        self.assertEqual({'casting'}, {event['source'] for event in self.supervisor.journal.events(1)})

    def test_stop_publishes_the_end_events_before_it_waits_for_instances(self):
        self.configure()
        sessions = {self.play('house'), self.play('office', device='0A:00:00:00:00:01')}
        self.target('house')['process'].stubborn = True
        self.published.clear()
        self.signals.clear()
        self.supervisor.stopped.set()
        started = time.monotonic()
        self.supervisor.run()
        self.assertLess(time.monotonic() - started, 4)
        pids = [process.pid for process in self.processes]
        ends = [message for topic, message, qos in self.published if message['action'] == 'end']
        self.assertEqual(sessions, {message['sessionId'] for message in ends})
        # Every end event and the withdrawn status are out before the first instance gets SIGTERM.
        first_signal = next(index for index, entry in enumerate(self.signals) if entry[0] != 'publish')
        self.assertEqual(len(self.published) + len(self.retained), first_signal)
        signals = [entry for entry in self.signals if entry[0] != 'publish']
        self.assertEqual([('TERM', pids[0]), ('TERM', pids[1])], signals[:2])
        self.assertEqual([('KILL', pids[0])], [entry for entry in signals if entry[0] == 'KILL'])
        self.assertEqual([('CastingStatus', {'available': False}, 1)], self.retained)
        self.assertTrue(all(qos == 1 for topic, message, qos in self.published))
        self.assertEqual(len(self.published) + 1, len(self.waits))
        self.assertTrue(all(0 <= timeout <= 1.5 for timeout in self.waits))

    def test_the_status_offers_airplay_only_while_the_supervisor_runs(self):
        self.supervisor.message('connected', {})
        self.assertEqual([('CastingStatus', {'available': True}, 1)], self.retained)
        # A stop withdraws AirPlay even before any configuration arrived.
        self.supervisor.stopped.set()
        self.supervisor.run()
        self.assertEqual(('CastingStatus', {'available': False}, 1), self.retained[-1])
        self.assertTrue(self.supervisor.withdrawn)
        # Without the broker nothing is withdrawn, so main leaves it to the will.
        self.online = False
        second = self.make_supervisor()
        second.stopped.set()
        second.run()
        self.assertFalse(second.withdrawn)

    def test_main_keeps_the_will_unless_the_status_was_withdrawn(self):
        for withdrawn in (True, False):
            calls = []

            class FakeClient:
                def __init__(self, *arguments, **options):
                    pass

                def __getattr__(self, name):
                    return lambda *arguments, **options: calls.append((name, arguments, options))

            class FakeSupervisor:
                def __init__(self, root, runtime, publish, **options):
                    self.publish, self.withdrawn = publish, withdrawn
                    self.incoming, self.stopped = None, threading.Event()

                def run(self):
                    self.publish('CastingStatus', {'available': False}, 1, True)

            client = types.SimpleNamespace(Client=FakeClient, CallbackAPIVersion=types.SimpleNamespace(VERSION2=2))
            paho = types.SimpleNamespace(mqtt=types.SimpleNamespace(client=client))
            with patch.dict(sys.modules, {'paho': paho, 'paho.mqtt': paho.mqtt, 'paho.mqtt.client': client}), \
                    patch('casting_supervisor.CastingSupervisor', FakeSupervisor), \
                    patch('casting_supervisor.signal.signal'), patch('casting_supervisor.os.umask'):
                casting_supervisor.main()
            names = [name for name, arguments, options in calls]
            will = next((arguments, options) for name, arguments, options in calls if name == 'will_set')
            self.assertEqual((('SyrenSystem/v3/CastingStatus', '{"available": false}'), {'qos': 1, 'retain': True}), will)
            self.assertLess(names.index('will_set'), names.index('connect_async'))
            self.assertIn(('publish', ('SyrenSystem/v3/CastingStatus', '{"available": false}'), {'qos': 1, 'retain': True}),
                          calls)
            self.assertEqual(withdrawn, 'disconnect' in names)

    def test_a_slow_instance_never_holds_up_the_loop_and_its_slot_waits(self):
        self.configure()
        target = self.target()
        first, house = target['process'], self.target('house')['process']
        first.stubborn = True
        session = self.play()
        self.supervisor.message('Catalogue', {'generation': 1, 'revision': 1, 'sessions': [
            {'id': session, 'state': 'ended', 'eventSequence': 2}]})
        self.assertIn(('TERM', first.pid), self.signals)
        self.assertNotIn(('wait', first.pid), self.signals)
        for second in range(2):
            self.supervisor.reconcile()
            self.assertIsNone(target['process'])
            self.clock.now += 1
        self.assertNotIn(('KILL', first.pid), self.signals)
        self.supervisor.reconcile()
        self.assertIn(('KILL', first.pid), self.signals)
        self.assertIsNotNone(target['process'])
        self.assertIsNot(first, target['process'])
        self.assertIs(house, self.target('house')['process'])
        self.assertEqual([], self.supervisor.stopping)

    def test_a_new_room_waits_for_a_stopping_instance_in_its_slot(self):
        self.configure()
        office = self.target()
        old = office['process']
        old.stubborn = True
        slot = office['specification'].slot
        self.configure(revision=2, groups=[{'id': 'kitchen', 'name': 'Keuken', 'enabledSources': ['casting']}])
        kitchen = self.target('kitchen')
        self.assertEqual(slot, kitchen['specification'].slot)
        self.assertIsNone(kitchen['process'])
        old.stubborn = False
        old.returncode = -15
        self.clock.now += 1
        self.supervisor.reconcile()
        self.assertIsNotNone(kitchen['process'])

    def test_a_halted_instance_keeps_no_half_line(self):
        self.configure()
        target = self.target()
        specification, token, slot = target['specification'], target['token'], target['specification'].slot
        self.supervisor.output.put((specification.identity, token, 'stderr', slot, b'warning: half'))
        self.deliver()
        self.assertEqual({(specification.identity, token)}, set(self.supervisor.stderr_buffers))
        self.supervisor.halt(target, self.clock())
        self.assertEqual({}, self.supervisor.stderr_buffers)
        self.assertIn(f'airplay[{slot}] warning: half', self.logs)
        # Output the reader queued before the pipe closed is written out at once, never kept.
        self.supervisor.output.put((specification.identity, token, 'stderr', slot, b'late\npartial'))
        self.deliver()
        self.assertEqual({}, self.supervisor.stderr_buffers)
        self.assertIn(f'airplay[{slot}] partial', self.logs)

    def test_spotify_and_pc_streams_are_never_removed(self):
        self.snap.status = {'spotify-abc': 'playing', 'pc-1': 'idle', 'syren-priority': 'idle', 'default': 'idle',
                            'airplay-stale': 'idle'}
        self.configure()
        self.assertEqual([{'id': 'airplay-stale'}], [parameters for method, parameters in self.snap.calls
                                                      if method == 'Stream.RemoveStream'])
        self.configure(revision=2, groups=[{'id': 'office', 'name': 'Kantoor', 'enabledSources': ['spotify']}])
        removed = {parameters['id'] for method, parameters in self.snap.calls if method == 'Stream.RemoveStream'}
        self.assertEqual({'airplay-stale', 'airplay-' + self.identity('house'), 'airplay-' + self.identity('office')}, removed)
        self.assertEqual({'spotify-abc', 'pc-1', 'syren-priority', 'default'}, set(self.snap.status))
        self.assertEqual({}, self.supervisor.targets)

    def test_the_reader_drains_metadata_and_logs(self):
        self.configure()
        target = self.target()
        reader = threading.Thread(target=self.supervisor.read_streams)
        reader.start()
        try:
            writer = os.open(target['specification'].meta, os.O_WRONLY | os.O_NONBLOCK)
            try:
                for code in connect():
                    os.write(writer, item(*code))
            finally:
                os.close(writer)
            os.write(target['process'].stderr_writer, b'warning: something\n')
            for attempt in range(100):
                self.supervisor.step()
                if self.actions() and self.logs:
                    break
                time.sleep(0.02)
        finally:
            self.supervisor.reader_stopped.set()
            reader.join(2)
        self.assertFalse(reader.is_alive())
        self.assertEqual(['start'], self.actions())
        self.assertIn(f'airplay[{target["specification"].slot}] warning: something', self.logs)


if __name__ == '__main__':
    unittest.main()
