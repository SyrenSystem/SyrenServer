import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from spotify_sessions import LifecycleJournal, ReceiverSpec, personal_receivers, realtime_command
from test_spotify_control import FakeSnap


class SpotifySessionsTests(unittest.TestCase):
    def test_audio_processes_use_the_real_time_limit(self):
        with patch('spotify_sessions.os.geteuid', return_value=1000):
            with patch('spotify_sessions.resource.getrlimit', return_value=(95, 95)):
                self.assertEqual(['chrt', '--fifo', '85', 'snapserver'], realtime_command(['snapserver'], 85))
            with patch('spotify_sessions.resource.getrlimit', return_value=(50, 50)):
                self.assertEqual(['chrt', '--fifo', '50', 'snapserver'], realtime_command(['snapserver'], 85))
            with patch('spotify_sessions.resource.getrlimit', return_value=(0, 0)):
                self.assertEqual(['snapserver'], realtime_command(['snapserver'], 85))

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / 'journal.json'
        self.specification = ReceiverSpec('receiver', 'person', 'account', 'house', 'SyrenSystem')
        self.journal = LifecycleJournal(self.path)

    def intent(self, action, command):
        self.journal.handle(self.specification, {'event': 'syren_intent', 'action': action,
                                               'commandId': command, 'accountId': 'account'})

    def test_duplicate_intent_after_restart_cannot_reclaim(self):
        self.intent('play', 'first')
        self.intent('pause', 'second')
        self.journal = LifecycleJournal(self.path)
        self.intent('play', 'first')
        self.assertEqual(['start', 'play', 'pause'], [event['action'] for event in self.journal.events(1)])

    def test_decoder_events_never_claim(self):
        self.intent('play', 'first')
        for event in ('loading', 'playing', 'loading', 'playing'):
            self.journal.handle(self.specification, {'event': event})
        self.assertEqual(1, sum(event['action'] == 'play' for event in self.journal.events(1)))

    def test_loading_a_new_track_keeps_the_transport(self):
        self.intent('play', 'first')
        self.journal.handle(self.specification, {'event': 'playing'})
        self.journal.handle(self.specification, {'event': 'loading'})
        events = self.journal.events(1)
        self.assertNotIn('interrupt', [event['action'] for event in events])
        self.assertTrue(events[-1]['transports'][0]['available'])
        self.journal.handle(self.specification, {'event': 'stopped'})
        self.assertEqual('interrupt', self.journal.events(1)[-1]['action'])

    def test_new_transfer_after_end_uses_new_session(self):
        self.intent('play', 'first')
        first = self.journal.events(1)[0]['sessionId']
        self.intent('end', 'second')
        self.intent('play', 'third')
        self.assertNotEqual(first, self.journal.events(1)[-1]['sessionId'])

    def test_acknowledgment_keeps_unacknowledged_events(self):
        self.intent('play', 'first')
        session = self.journal.events(1)[0]['sessionId']
        self.journal.acknowledge({'sessions': [{'id': session, 'eventSequence': 1}]})
        self.assertEqual(['play'], [event['action'] for event in self.journal.events(2)])
        self.assertEqual(2, self.journal.events(2)[0]['generation'])
        self.assertEqual(0o600, self.path.stat().st_mode & 0o777)

    def test_rename_and_preferences_preserve_identity(self):
        configuration = {'stateId': 'home', 'profiles': [{'id': 'person', 'spotifyAccountId': 'account'}],
                         'groups': [{'id': 'kitchen', 'name': 'Kitchen', 'enabledSources': ['spotify']}]}
        before = personal_receivers(configuration)
        configuration['groups'][0]['name'] = 'Cooking'
        after = personal_receivers(configuration)
        self.assertEqual([receiver.identity for receiver in before], [receiver.identity for receiver in after])
        self.assertEqual('SyrenSystem · Cooking', after[1].name)
        configuration['groups'][0]['enabledSources'] = ['laptop']
        self.assertEqual([before[0]], personal_receivers(configuration))

    def test_spotify_events_and_stream_uri_are_unchanged(self):
        self.intent('play', 'first')
        session = self.journal.receiver('receiver')['sessionId']
        transports = [{'id': 'spotify-receiver', 'kind': 'snapcast', 'endpoint': 'spotify-receiver', 'available': True}]
        common = {'sessionId': session, 'producerId': 'receiver', 'source': 'spotify', 'ownerId': 'person',
                  'accountId': 'account', 'destination': 'house', 'transports': transports, 'generation': 1}
        self.assertEqual([dict(common, eventSequence=1, action='start'), dict(common, eventSequence=2, action='play')],
                         self.journal.events(1))
        self.journal.acknowledge({'sessions': [{'id': session, 'eventSequence': 2}]})
        self.assertEqual({'sessionId': session, 'producerId': 'receiver', 'eventSequence': 2, 'action': 'heartbeat',
                          'source': 'spotify', 'generation': 1}, self.journal.heartbeat(self.specification, 1))
        snap = FakeSnap()
        requests = []
        record = snap.request
        snap.request = lambda method, parameters=None: requests.append((method, parameters)) or record(method, parameters)
        fifo = Path(self.temporary.name) / 'spotify-receiver.fifo'
        snap.ensure_stream(self.specification, fifo, set())
        self.assertEqual([('Stream.AddStream', {'streamUri': f'pipe://{fifo}?name=spotify-receiver'
                                                             '&sampleformat=44100:16:2&codec=pcm'})], requests)
        self.assertEqual({'spotify-receiver'}, snap.stream_ids)


if __name__ == '__main__':
    unittest.main()
