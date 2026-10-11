import importlib.util
import os
from pathlib import Path
import signal
import subprocess
import unittest
from unittest.mock import patch

import spotify_supervisor


def load():
    specification = importlib.util.spec_from_file_location('start_profile_audio', Path(__file__).with_name('start-profile-audio.py'))
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    return module


class FakeTime:
    def __init__(self, test):
        self.test = test
        self.now = 0.0

    def monotonic(self):
        return self.now

    def sleep(self, seconds):
        self.now = round(self.now + seconds, 6)
        self.test.tick()


class FakeChild:
    def __init__(self, test, command, **options):
        self.test = test
        self.command = command
        self.pid = len(test.children) + 1
        self.name = ('server' if any('snapserver' in part for part in command) else
                     'casting' if any('casting' in part for part in command) else 'spotify')
        self.started = test.clock.now
        self.returncode = None
        self.stubborn = False
        test.children.append(self)

    def exit(self, code):
        self.test.zombies[self.pid] = code

    def poll(self):
        if self.returncode is None and self.pid in self.test.zombies:
            self.returncode = self.test.zombies.pop(self.pid)
        return self.returncode

    def terminate(self):
        self.test.events.append((self.test.clock.now, 'TERM', self.name))
        if not self.stubborn:
            self.exit(-15)

    def kill(self):
        self.test.events.append((self.test.clock.now, 'KILL', self.name))
        self.exit(-9)

    def wait(self, timeout=None):
        self.test.events.append((self.test.clock.now, 'wait', self.name))
        if self.poll() is None:
            self.test.clock.now = round(self.test.clock.now + timeout, 6)
            raise subprocess.TimeoutExpired(self.command, timeout)
        return self.returncode


class StartProfileAudioTests(unittest.TestCase):
    def setUp(self):
        self.module = load()
        self.clock = FakeTime(self)
        self.children = []
        self.zombies = {}
        self.events = []
        self.printed = []
        self.handlers = {}
        self.reaped = []
        self.peeks = []
        self.scenario = lambda: None
        self.module.print = lambda message, flush=False: self.printed.append(message)
        for target, name, value in ((self.module, 'time', self.clock), (spotify_supervisor, 'time', self.clock),
                                    (self.module, 'write_configuration', lambda: None),
                                    (self.module.subprocess, 'Popen', lambda command, **options: FakeChild(self, command, **options)),
                                    (self.module.signal, 'signal', lambda number, handler: self.handlers.__setitem__(number, handler)),
                                    (self.module.os, 'waitid', self.waitid), (self.module.os, 'waitpid', self.waitpid)):
            patcher = patch.object(target, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def waitid(self, kind, identifier, options):
        self.peeks.append((kind, identifier, options))
        if not self.zombies:
            return None
        pid = next(iter(self.zombies))
        return os.waitid_result((pid, 0, 0, os.CLD_EXITED, self.zombies[pid]))

    def waitpid(self, pid, options):
        self.reaped.append((pid, options))
        return pid, self.zombies.pop(pid) << 8

    def tick(self):
        self.scenario()

    def child(self, name):
        return [child for child in self.children if child.name == name][-1]

    def stop_at(self, moment):
        if self.clock.now >= moment:
            self.handlers[signal.SIGTERM](signal.SIGTERM, None)

    def run_main(self, casting='1'):
        with patch.dict(os.environ, {'SYREN_CASTING': casting} if casting is not None else {}, clear=False):
            if casting is None:
                os.environ.pop('SYREN_CASTING', None)
            return self.module.main()

    def test_casting_crash_restarts_only_casting_with_backoff(self):
        lifetimes = [1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 61, 0.1]

        def scenario():
            casting = [child for child in self.children if child.name == 'casting']
            current = casting[-1]
            if current.returncode is None and current.pid not in self.zombies and \
                    self.clock.now - current.started >= lifetimes[len(casting) - 1] - 1e-9:
                if len(casting) == len(lifetimes):
                    self.handlers[signal.SIGTERM](signal.SIGTERM, None)
                else:
                    current.exit(1)

        self.scenario = scenario
        self.assertEqual(0, self.run_main())
        casting = [child for child in self.children if child.name == 'casting']
        self.assertEqual(9, len(casting))
        self.assertEqual(['server', 'spotify'], [child.name for child in self.children[:2]])
        self.assertEqual(11, len(self.children))
        delays = [int(message.split('restarting in ')[1].split(' ')[0]) for message in self.printed]
        self.assertEqual([1, 2, 4, 8, 16, 30, 30, 1], delays)
        self.assertTrue(all(message.startswith('Casting supervisor exited with 1; restarting in ') for message in self.printed))
        gaps = [round(later.started - earlier.started - lifetime, 1)
                for earlier, later, lifetime in zip(casting, casting[1:], lifetimes)]
        self.assertEqual([1.0, 2.0, 4.0, 8.0, 16.0, 30.0, 30.0, 1.0], gaps)
        self.assertEqual({self.clock.now}, {event[0] for event in self.events if event[1] == 'TERM'})

    def test_snapserver_exit_still_stops_everything(self):
        for failing, stopped in (('server', ['casting', 'spotify']), ('spotify', ['casting', 'server'])):
            self.children.clear()
            self.events.clear()

            def scenario():
                if self.clock.now >= 0.5 and self.child(failing).poll() is None:
                    self.child(failing).exit(1)

            self.scenario = scenario
            self.assertEqual(1, self.run_main())
            self.assertEqual(['server', 'spotify', 'casting'], [child.name for child in self.children])
            self.assertTrue(all(child.poll() is not None for child in self.children))
            self.assertEqual(stopped, [event[2] for event in self.events if event[1] == 'TERM'])

    def test_orphans_are_reaped_and_children_keep_their_exit_codes(self):
        def scenario():
            casting = self.child('casting')
            if self.clock.now == 0.3:
                self.zombies[999] = 0
            elif self.clock.now == 0.6:
                casting.exit(3)
                self.zombies[1000] = 0
            elif self.clock.now >= 5:
                self.handlers[signal.SIGTERM](signal.SIGTERM, None)

        self.scenario = scenario
        self.assertEqual(0, self.run_main())
        self.assertEqual([(999, 0), (1000, 0)], self.reaped)
        self.assertEqual('Casting supervisor exited with 3; restarting in 1 s', self.printed[0])
        self.assertEqual({(os.P_ALL, 0, os.WEXITED | os.WNOHANG | os.WNOWAIT)}, set(self.peeks))
        self.assertEqual(3, self.children[2].returncode)
        self.assertEqual({}, self.zombies)
        # A known child that exited is only peeked at, so its own poll still reads its exit code.
        known = FakeChild(self, ['casting_supervisor.py'])
        known.exit(7)
        self.zombies[2000] = 0
        self.module.reap_orphans({known.pid})
        self.assertEqual([(999, 0), (1000, 0)], self.reaped)
        self.assertEqual(7, known.poll())
        self.module.reap_orphans({known.pid})
        self.assertEqual((2000, 0), self.reaped[-1])

    def test_shutdown_keeps_spotify_timing_and_bounds_casting(self):
        self.scenario = lambda: self.stop_at(1)
        self.run_main(None)
        spotify_alone = [event for event in self.events if event[2] != 'casting']
        self.children.clear()
        self.events.clear()
        self.clock.now = 0.0

        def stubborn_casting():
            self.child('casting').stubborn = True
            self.stop_at(1)

        self.scenario = stubborn_casting
        self.assertEqual(0, self.run_main())
        stop = 1.0
        # The shared stop helper repeats SIGTERM to a casting supervisor that still runs, which only sets its stop flag again.
        self.assertEqual([(stop, 'TERM', 'casting'), (stop, 'TERM', 'spotify'), (stop, 'wait', 'spotify'),
                          (stop, 'TERM', 'casting'), (stop, 'wait', 'casting'), (stop + 4, 'KILL', 'casting'),
                          (stop + 4, 'wait', 'casting'), (stop + 4, 'TERM', 'server'), (stop + 4, 'wait', 'server')],
                         self.events)
        self.assertEqual(spotify_alone[:2], [event for event in self.events if event[2] == 'spotify'])
        self.children.clear()
        self.events.clear()
        self.clock.now = 0.0

        def everything_stubborn():
            for child in self.children:
                child.stubborn = True
            self.stop_at(1)

        self.scenario = everything_stubborn
        self.run_main()
        self.assertLess(self.clock.now - stop, 10)
        self.assertEqual('server', self.events[-1][2])

    def test_casting_is_off_without_the_switch(self):
        for value in (None, '0'):
            self.children.clear()
            self.events.clear()
            self.peeks.clear()
            self.clock.now = 0.0
            self.scenario = lambda: self.stop_at(30)
            self.assertEqual(0, self.run_main(value))
            self.assertEqual(['server', 'spotify'], [child.name for child in self.children])
            self.assertEqual([], self.peeks)
            self.assertEqual([], self.printed)
            self.assertEqual([(30.0, 'TERM', 'spotify'), (30.0, 'wait', 'spotify'), (30.0, 'TERM', 'server'),
                              (30.0, 'wait', 'server')], self.events)


if __name__ == '__main__':
    unittest.main()
