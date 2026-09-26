import argparse
import array
import json
import os
from pathlib import Path
import tempfile
import unittest

import spotify_destination_probe as probe


class DestinationProbeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.state = Path(self.temporary.name)
        self.arguments = argparse.Namespace(image='image-under-test', profiles=['first', 'second'],
                                            destination_id='house')

    def credentials(self, profile, username):
        cache = probe.private_directory(self.state / 'accounts' / profile)
        credentials = {'username': username, 'auth_type': 1, 'auth_data': 'test-only-not-a-token'}
        path = cache / 'credentials.json'
        path.write_text(json.dumps(credentials))
        path.chmod(0o600)
        return cache

    def test_personal_destinations_have_distinct_stable_ids(self):
        first = probe.device_id('household', 'first', 'house')
        second = probe.device_id('household', 'second', 'house')
        self.assertNotEqual(first, second)
        self.assertNotEqual(first, probe.device_id('household', 'first', 'kitchen'))
        self.assertNotEqual(first, probe.device_id('another-household', 'first', 'house'))
        self.assertRegex(first, r'^[0-9a-f]{40}$')
        self.assertEqual(first, probe.device_id('household', 'first', 'house'))

    def test_namespace_survives_restart(self):
        self.assertEqual(probe.namespace_for(self.state), probe.namespace_for(self.state))

    def test_invalid_identifiers_cannot_escape_storage(self):
        for value in ['../first', '/tmp/first', '', 'with space', 'a' * 65]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                probe.identifier(value)

    def test_same_account_cannot_pass_as_two_profiles(self):
        self.credentials('first', 'same-account')
        self.credentials('second', 'same-account')
        with self.assertRaisesRegex(ValueError, 'two different Spotify accounts'):
            probe.prepare_receivers(self.arguments, self.state, 'household')

    def test_duplicate_profile_is_rejected(self):
        self.arguments.profiles = ['first', 'first']
        with self.assertRaises(ValueError):
            probe.prepare_receivers(self.arguments, self.state, 'household')

    def test_receiver_caches_are_private_and_isolated(self):
        self.credentials('first', 'account-one')
        self.credentials('second', 'account-two')
        receivers = probe.prepare_receivers(self.arguments, self.state, 'household')
        self.assertEqual(len({cache for _, _, cache in receivers}), 2)
        for profile, _, cache in receivers:
            self.assertEqual(cache.stat().st_mode & 0o777, 0o700)
            self.assertEqual((cache / 'credentials.json').stat().st_mode & 0o777, 0o600)
            self.assertEqual(probe.read_credentials(cache), probe.read_credentials(self.state / 'accounts' / profile))

    def test_read_rejects_public_credentials_and_symlinks(self):
        cache = self.credentials('first', 'account-one')
        path = cache / 'credentials.json'
        path.chmod(0o644)
        with self.assertRaises(ValueError):
            probe.read_credentials(cache)
        path.rename(cache / 'private.json')
        path.symlink_to(cache / 'private.json')
        with self.assertRaises(OSError):
            probe.read_credentials(cache)

    def test_receiver_command_disables_local_discovery_and_keeps_credentials_out_of_arguments(self):
        command = probe.command_for(self.arguments, self.state, '1' * 40, 'SyrenSystem', 'test-container')
        self.assertIn('--disable-discovery', command)
        self.assertNotIn('--access-token', command)
        self.assertNotIn('--password', command)
        self.assertNotIn('--enable-device-auth', command)
        self.assertEqual(command[command.index('--device-id') + 1], '1' * 40)
        self.assertEqual(command[command.index('--name', command.index(self.arguments.image)) + 1], 'SyrenSystem')

    def test_linking_cannot_write_pcm_to_the_pairing_terminal(self):
        command = probe.command_for(self.arguments, self.state, '1' * 40, 'Link', 'test', linking=True)
        self.assertIn('--enable-device-auth', command)
        self.assertEqual(command[-2:], ['--device', '/dev/null'])


class ConcurrentAudioTests(unittest.TestCase):
    def payload(self, amplitude=1000):
        samples = array.array('h', [amplitude, -amplitude] * 22050)
        if probe.sys.byteorder != 'little':
            samples.byteswap()
        return samples.tobytes()

    def test_fragmented_pcm_preserves_frames_and_energy(self):
        evidence = probe.AudioEvidence()
        payload = self.payload()
        evidence.receive(payload[:13], 1)
        evidence.receive(payload[13:], 1)
        self.assertEqual(evidence.active_seconds(), {1})
        self.assertEqual(evidence.summary()['samples'], 44100)
        self.assertEqual(evidence.pending, b'')

    def test_digital_silence_and_dither_do_not_count(self):
        for amplitude in [0, 1, 31]:
            evidence = probe.AudioEvidence()
            evidence.receive(self.payload(amplitude), 0)
            self.assertEqual(evidence.active_seconds(), set())

    def test_a_short_burst_does_not_count_as_a_full_second(self):
        evidence = probe.AudioEvidence()
        evidence.receive(self.payload()[:2000], 0)
        self.assertEqual(evidence.active_seconds(), set())

    def test_sequential_playback_is_not_concurrent_playback(self):
        first, second = probe.AudioEvidence(), probe.AudioEvidence()
        for index in range(12):
            first.receive(self.payload(), index)
            second.receive(self.payload(), index + 12)
        self.assertEqual(probe.longest_overlap([first, second]), 0)

    def test_continuous_overlap_requires_both_live_receivers(self):
        first, second = probe.AudioEvidence(), probe.AudioEvidence()
        for index in range(20):
            first.receive(self.payload(), index)
            if index != 10:
                second.receive(self.payload(), index)
        self.assertEqual(probe.longest_overlap([first, second]), 10)


class PcmPacingTests(unittest.TestCase):
    def test_unbounded_input_cannot_advance_faster_than_playback(self):
        pacer = probe.PcmPacer()
        received = 0
        for tick in range(5000):
            now = tick / 1000
            amount = pacer.read_size(now)
            received += amount
            pacer.consume(amount, now)
        self.assertGreaterEqual(received, 176400 * 4.9)
        self.assertLessEqual(received, 176400 * 5 + 3528)

    def test_a_pause_does_not_accumulate_a_fast_forward_allowance(self):
        pacer = probe.PcmPacer()
        amount = pacer.read_size(0)
        pacer.consume(amount, 0)
        self.assertLessEqual(pacer.read_size(300), 3528)
        pacer.consume(3528, 300)
        self.assertEqual(pacer.read_size(300), 0)

    def test_fragmented_reads_consume_only_the_bytes_received(self):
        pacer = probe.PcmPacer()
        pacer.consume(1764, 0)
        self.assertEqual(pacer.read_size(0.005), 0)
        self.assertEqual(pacer.read_size(0.010), 3528)


if __name__ == '__main__':
    unittest.main()
