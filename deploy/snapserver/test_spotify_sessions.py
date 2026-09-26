import tempfile
import unittest
from pathlib import Path

from spotify_sessions import LifecycleJournal, ReceiverSpec, personal_receivers


class SpotifySessionsTests(unittest.TestCase):
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


if __name__ == '__main__':
    unittest.main()
