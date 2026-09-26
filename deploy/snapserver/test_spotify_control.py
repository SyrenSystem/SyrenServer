import json
import os
import socket
import threading
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

from spotify_supervisor import SnapControl, SpotifySupervisor, linked_credentials
from spotify_sessions import atomic_json, personal_receivers


class FakeProcess:
    def __init__(self, command, **options):
        self.command = command
        self.stdout = None

    def poll(self):
        return None

    def terminate(self):
        pass

    def wait(self, timeout=None):
        return 0


class FakeSnap(SnapControl):
    def __init__(self):
        super().__init__()
        self.methods = []
        self.stream_ids = set()

    def request(self, method, parameters=None):
        self.methods.append(method)
        if method == 'Server.GetStatus':
            return {'server': {'streams': [{'id': identity} for identity in self.stream_ids]}}
        if method == 'Stream.AddStream':
            self.stream_ids.add(parameters['streamUri'].split('name=')[1].split('&')[0])
        return {}


class SupervisorTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.online = True
        self.published = []
        self.snap = FakeSnap()
        self.supervisor = SpotifySupervisor(self.directory / 'state', self.directory / 'run',
                                            lambda topic, message, qos=1: self.published.append((topic, message, qos)),
                                            snapcast=self.snap, connected=lambda: self.online, discovery='192.0.2.5')
        self.addCleanup(self.supervisor.socket.close)
        self.configuration = {'protocolVersion': 3, 'stateId': 'home', 'generation': 1, 'revision': 1, 'groups': [],
                              'profiles': [{'id': 'first', 'spotifyAccountId': 'one'},
                                           {'id': 'second', 'spotifyAccountId': 'two'}]}
        for profile in self.configuration['profiles']:
            atomic_json(self.supervisor.account_directory(profile['id']) / 'credentials.json',
                        {'username': profile['spotifyAccountId'], 'auth_data': 'fixture'})
        patcher = patch('spotify_supervisor.subprocess.Popen', FakeProcess)
        patcher.start()
        self.addCleanup(patcher.stop)

    def configure(self, **changes):
        self.configuration = dict(self.configuration, **changes)
        self.supervisor.message('Configuration', self.configuration)
        self.supervisor.reconcile()

    def personal(self, profile):
        return next(specification for specification in personal_receivers(self.configuration)
                    if specification.profile == profile)

    def lifecycle(self):
        return [(message['action'], qos) for topic, message, qos in self.published if topic == 'Lifecycle']

    def test_one_failing_receiver_does_not_block_the_others(self):
        (self.supervisor.account_directory('first') / 'credentials.json').chmod(0o644)
        self.configure()
        self.assertNotIn(self.personal('first').identity, self.supervisor.processes)
        self.assertIn(self.personal('second').identity, self.supervisor.processes)
        self.assertEqual(2, len(self.supervisor.processes))

    def test_reconcile_reads_stream_status_once(self):
        self.configure()
        self.supervisor.reconcile()
        self.assertEqual(3, len(self.supervisor.processes))
        self.assertEqual(2, self.snap.methods.count('Server.GetStatus'))

    def test_events_wait_for_a_connection_and_heartbeats_are_not_journaled(self):
        self.configure()
        specification = self.personal('first')
        self.supervisor.journal.handle(specification, {'event': 'syren_intent', 'action': 'play', 'commandId': 'one'})
        self.online = False
        self.supervisor.publish_pending()
        self.supervisor.prepare_heartbeats()
        self.supervisor.publish_heartbeats()
        self.assertEqual([], self.published)
        self.online = True
        self.supervisor.publish_pending()
        self.supervisor.publish_pending()
        self.supervisor.prepare_heartbeats()
        self.supervisor.publish_heartbeats()
        self.assertEqual([('start', 1), ('play', 1)], self.lifecycle())
        self.supervisor.message('connected', {})
        self.supervisor.publish_pending()
        self.assertEqual(4, len(self.lifecycle()))
        session = self.supervisor.journal.receiver(specification.identity)['sessionId']
        self.supervisor.message('Catalogue', {'generation': 1, 'revision': 1, 'sessions': [
            {'id': session, 'state': 'playing', 'eventSequence': 2}]})
        journal = (self.directory / 'state' / 'lifecycle.json').read_bytes()
        self.supervisor.prepare_heartbeats()
        self.supervisor.publish_heartbeats()
        self.assertEqual(('heartbeat', 0), self.lifecycle()[-1])
        self.assertEqual(2, self.published[-1][1]['eventSequence'])
        self.assertEqual(journal, (self.directory / 'state' / 'lifecycle.json').read_bytes())

    def playing_session(self):
        self.configure()
        specification = self.personal('first')
        self.supervisor.journal.handle(specification, {'event': 'syren_intent', 'action': 'play', 'commandId': 'one'})
        session = self.supervisor.journal.receiver(specification.identity)['sessionId']
        self.supervisor.message('Catalogue', {'generation': 1, 'revision': 1, 'sessions': [
            {'id': session, 'state': 'playing', 'eventSequence': 2}]})
        return session

    def test_a_failing_reconcile_keeps_heartbeats_and_backs_off(self):
        session = self.playing_session()
        self.supervisor.journal.handle(self.personal('second'), {'event': 'syren_intent', 'action': 'play', 'commandId': 'two'})

        def unavailable(method, parameters=None):
            raise ConnectionError('Snapserver closed control connection')

        self.snap.request = unavailable
        self.supervisor.next_reconcile = 0
        delays = []
        for attempt in range(3):
            self.supervisor.step()
            delays.append(self.supervisor.reconcile_delay)
            self.supervisor.next_reconcile = 0
        self.assertEqual([2, 4, 8], delays)
        self.assertIn(('play', 1), self.lifecycle())
        self.supervisor.publish_heartbeats()
        self.assertEqual(('heartbeat', 0), self.lifecycle()[-1])
        self.assertEqual(session, self.published[-1][1]['sessionId'])

    def test_heartbeats_continue_while_reconcile_is_blocked(self):
        session = self.playing_session()
        entered, release = threading.Event(), threading.Event()

        def blocked(method, parameters=None):
            entered.set()
            release.wait(5)
            raise TimeoutError('Snapserver did not answer in time')

        self.snap.request = blocked
        self.supervisor.next_reconcile = 0
        self.published.clear()
        runner = threading.Thread(target=self.supervisor.run)
        runner.start()
        try:
            self.assertTrue(entered.wait(2))
            for attempt in range(40):
                if any(message.get('action') == 'heartbeat' for topic, message, qos in list(self.published)):
                    break
                release.wait(.05)
            self.assertFalse(release.is_set())
            self.assertIn(('heartbeat', 0), self.lifecycle())
            self.assertEqual(session, next(message['sessionId'] for topic, message, qos in self.published
                                           if message.get('action') == 'heartbeat'))
        finally:
            self.supervisor.stopped.set()
            release.set()
            runner.join(5)
        self.assertFalse(runner.is_alive())

    def test_a_rejected_session_stops_being_resent(self):
        self.configure()
        specification = self.personal('first')
        self.supervisor.journal.handle(specification, {'event': 'syren_intent', 'action': 'play', 'commandId': 'one'})
        session = self.supervisor.journal.receiver(specification.identity)['sessionId']
        self.supervisor.message('LifecycleRejected', {'generation': 1, 'sessionId': session, 'producerId': 'another'})
        self.assertEqual(2, len(self.supervisor.journal.events(1)))
        self.supervisor.message('LifecycleRejected', {'generation': 1, 'sessionId': session,
                                                      'producerId': specification.identity})
        self.assertEqual([], self.supervisor.journal.events(1))
        self.assertIsNone(self.supervisor.journal.receiver(specification.identity)['sessionId'])
        self.supervisor.journal.handle(specification, {'event': 'syren_intent', 'action': 'play', 'commandId': 'two'})
        self.assertNotEqual(session, self.supervisor.journal.events(1)[0]['sessionId'])

    def test_unlinking_removes_stored_credentials(self):
        self.configure()
        receiver = self.directory / 'state' / 'receivers' / self.personal('first').identity
        self.assertTrue((receiver / 'credentials.json').exists())
        first = self.supervisor.account_directory('first')
        self.configure(revision=2, profiles=[{'id': 'first'}, {'id': 'second', 'spotifyAccountId': 'two'}])
        self.assertFalse((first / 'credentials.json').exists())
        self.assertFalse(receiver.exists())
        self.assertTrue((self.supervisor.account_directory('second') / 'credentials.json').exists())

    def test_guest_slots_are_released(self):
        self.configure()
        guests = self.supervisor.guests['home']
        guests[0].update(account='visitor', claimedAt=1)
        for index in range(5):
            self.supervisor.guests['home'].append({'identity': f'claimed-{index}', 'destination': 'house',
                                                   'account': f'guest-{index}', 'claimedAt': 10 + index})
        self.supervisor.guests['home'].append({'identity': 'removed-group', 'destination': 'group', 'account': None})
        self.configure(revision=2, profiles=self.configuration['profiles'] + [{'id': 'third', 'spotifyAccountId': 'guest-4'}])
        identities = [guest['identity'] for guest in self.supervisor.guests['home']]
        self.assertEqual(['claimed-3', 'claimed-2', 'claimed-1', 'claimed-0'], identities[:4])
        self.assertEqual(5, len(identities))
        self.assertTrue(all(identity in self.supervisor.processes for identity in identities))
        self.assertEqual(['home'], list(json.loads((self.directory / 'state' / 'guests.json').read_text())))

    def test_guest_receivers_use_the_discovery_address(self):
        self.configure()
        commands = [running['process'].command for running in self.supervisor.processes.values()]
        guest = next(command for command in commands if '--lock-discovery-account' in command)
        self.assertEqual('192.0.2.5', guest[guest.index('--zeroconf-interface') + 1])
        self.assertTrue(all('--zeroconf-interface' not in command for command in commands if command is not guest))

    def test_a_new_household_state_replaces_the_old_one(self):
        self.configure()
        old = set(self.supervisor.processes)
        self.supervisor.journal.handle(self.personal('first'), {'event': 'syren_intent', 'action': 'play', 'commandId': 'one'})
        self.configure(stateId='rebuilt', generation=1, revision=1)
        self.assertEqual('rebuilt', self.supervisor.configuration['stateId'])
        self.assertEqual([], self.supervisor.journal.events(1))
        self.assertFalse(old & set(self.supervisor.processes))
        self.assertEqual(3, len(self.supervisor.processes))

    def link(self, status):
        request = {'ticket': status, 'profileId': 'third', 'stateId': 'home', 'generation': 1}
        self.supervisor.links[status] = {'acknowledged': threading.Event(), 'status': status}
        self.supervisor.links[status]['acknowledged'].set()
        self.supervisor.finish_link(request, {'username': 'three', 'auth_data': 'fixture'})
        return self.supervisor.account_directory('third')

    def test_a_rejected_link_removes_its_pending_credentials(self):
        self.configure(profiles=self.configuration['profiles'] + [{'id': 'third'}])
        directory = self.link('rejected')
        self.assertFalse((directory / 'pending.json').exists())
        self.assertFalse((directory / 'credentials.json').exists())
        self.assertNotIn('failed', [message.get('status') for topic, message, qos in self.published])

    def test_a_confirmed_link_survives_a_reconcile_before_the_new_configuration(self):
        self.configure(profiles=self.configuration['profiles'] + [{'id': 'third'}])
        directory = self.link('linked')
        self.supervisor.reconcile()
        self.assertTrue((directory / 'pending.json').exists())
        self.configure(revision=2, profiles=self.configuration['profiles'][:2] + [{'id': 'third', 'spotifyAccountId': 'three'}])
        self.assertFalse((directory / 'pending.json').exists())
        self.assertEqual('three', json.loads((directory / 'credentials.json').read_text())['username'])
        self.assertIn(self.personal('third').identity, self.supervisor.processes)

    def test_partial_link_credentials_are_read_again(self):
        path = self.directory / 'credentials.json'
        self.assertIsNone(linked_credentials(path))
        path.write_text('{"username": "one", "auth')
        self.assertIsNone(linked_credentials(path))
        path.write_text(json.dumps({'username': 'one', 'auth_data': 'fixture'}))
        self.assertEqual('one', linked_credentials(path)['username'])
        self.assertEqual(0o600, path.stat().st_mode & 0o777)


class SnapControlTests(unittest.TestCase):
    def test_link_credentials_recover_after_lost_acknowledgment(self):
        with tempfile.TemporaryDirectory() as temporary:
            supervisor = object.__new__(SpotifySupervisor)
            supervisor.root = Path(temporary)
            supervisor.configuration = {'stateId': 'house', 'profiles': [{'id': 'person', 'spotifyAccountId': 'account'}]}
            pending = supervisor.account_directory('person') / 'pending.json'
            atomic_json(pending, {'stateId': 'house', 'credentials': {'username': 'account', 'auth_data': 'fixture'}})
            supervisor.finish_pending_links()
            self.assertFalse(pending.exists())
            stored = pending.parent / 'credentials.json'
            self.assertEqual('account', json.loads(stored.read_text())['username'])
            self.assertEqual(0o600, stored.stat().st_mode & 0o777)

    def test_pending_link_never_replaces_another_account(self):
        with tempfile.TemporaryDirectory() as temporary:
            supervisor = object.__new__(SpotifySupervisor)
            supervisor.root = Path(temporary)
            supervisor.configuration = {'stateId': 'house', 'profiles': [{'id': 'person', 'spotifyAccountId': 'different'}]}
            pending = supervisor.account_directory('person') / 'pending.json'
            atomic_json(pending, {'stateId': 'house', 'credentials': {'username': 'account', 'auth_data': 'fixture'}})
            supervisor.finish_pending_links()
            self.assertFalse((pending.parent / 'credentials.json').exists())

    def test_requests_share_one_connection_and_ignore_notifications(self):
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            listener.listen()
            listener.settimeout(2)
            received = []

            def serve():
                with listener.accept()[0] as connection:
                    connection.settimeout(2)
                    reader = connection.makefile('r')
                    for index in range(2):
                        request = json.loads(reader.readline())
                        received.append(request['method'])
                        connection.sendall((json.dumps({'method': 'Server.OnUpdate'}) + '\n' +
                                            json.dumps({'id': request['id'], 'result': index}) + '\n').encode())

            server = threading.Thread(target=serve)
            server.start()
            control = SnapControl(port=listener.getsockname()[1])
            try:
                self.assertEqual(0, control.request('Server.GetStatus'))
                self.assertEqual(1, control.request('Server.GetStatus'))
            finally:
                control.close()
                server.join(3)
            self.assertEqual(['Server.GetStatus', 'Server.GetStatus'], received)


if __name__ == '__main__':
    unittest.main()
