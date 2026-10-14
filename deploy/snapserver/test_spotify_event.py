import json
import os
from pathlib import Path
import socket
import tempfile
import unittest
from unittest.mock import patch

import spotify_event


class SpotifyEventTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.path = str(Path(temporary.name) / 'events.sock')
        self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
        self.addCleanup(self.listener.close)
        self.listener.bind(self.path)
        self.listener.setblocking(False)

    def forward(self, event, **variables):
        environment = dict(SYREN_RECEIVER_ID='receiver', SYREN_RECEIVER_INSTANCE='instance',
                           SYREN_EVENT_SOCKET=self.path, PLAYER_EVENT=event, **variables)
        with patch.dict(os.environ, environment, clear=True):
            spotify_event.main()
        try:
            return json.loads(self.listener.recv(8192))
        except BlockingIOError:
            return None

    def test_volume_changes_are_forwarded(self):
        message = self.forward('volume_changed', VOLUME='32768')
        self.assertEqual('volume_changed', message['event'])
        self.assertEqual('32768', message['volume'])
        self.assertEqual('instance', message['instanceId'])

    def test_loading_is_not_forwarded(self):
        self.assertIsNone(self.forward('loading'))
        self.assertEqual('stopped', self.forward('stopped')['event'])


if __name__ == '__main__':
    unittest.main()
