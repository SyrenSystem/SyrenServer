import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SNAPSERVER = Path(__file__).resolve().parent
ENABLE = SNAPSERVER.parent / 'enable-casting-host.sh'
IMAGE_COPIES = {
    'syren-nqptp.service': 'host/syren-nqptp.service',
    'syren-avahi-relay.service': 'host/syren-avahi-relay.service',
    'syrensystem-casting.sysusers.conf': 'host/syrensystem-casting.sysusers.conf',
    'syrensystem-casting.tmpfiles.conf': 'host/syrensystem-casting.tmpfiles.conf',
    'syren-avahi-relay.py': 'casting_dbus_relay.py',
}
STUB = '#!/bin/sh\nprintf "%s\\n" "$(basename "$0") $*" >> "$SYREN_TEST_CALLS"\n'


# The image build only has the snapserver directory, so these run on the host gate.
@unittest.skipUnless(ENABLE.exists(), 'the host scripts are not next to this test')
@unittest.skipIf(os.geteuid() == 0, 'the root installer must never really run from a test')
class EnableCastingHostTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.extracted = self.directory / 'extracted'
        self.extracted.mkdir()
        for name, repository in IMAGE_COPIES.items():
            shutil.copyfile(SNAPSERVER / repository, self.extracted / name)
        (self.extracted / 'nqptp').write_bytes(b'image binary')
        self.stubs = self.directory / 'bin'
        self.stubs.mkdir()
        for command in ('install', 'systemctl', 'ss', 'systemd-sysusers', 'systemd-tmpfiles', 'systemd-run', 'chmod'):
            (self.stubs / command).write_text(STUB)
            (self.stubs / command).chmod(0o755)
        # The installer insists on root, so the stub answers 0.
        (self.stubs / 'id').write_text('#!/bin/sh\necho 0\n')
        (self.stubs / 'id').chmod(0o755)
        self.calls = self.directory / 'calls'
        self.calls.touch()

    def run_installer(self):
        environment = dict(os.environ, PATH=f'{self.stubs}:{os.environ["PATH"]}', SYREN_TEST_CALLS=str(self.calls))
        result = subprocess.run(['sh', str(ENABLE), str(self.extracted), '100099'], capture_output=True, text=True,
                                env=environment, timeout=20)
        installs = [line.split()[1:] for line in self.calls.read_text().splitlines() if line.startswith('install ')]
        return result, installs

    def test_root_installs_only_repository_copies_and_the_image_nqptp(self):
        result, installs = self.run_installer()
        sources = [argument for arguments in installs for argument in arguments
                   if argument.startswith((str(self.extracted), str(SNAPSERVER)))]
        self.assertEqual([str(self.extracted / 'nqptp')], [source for source in sources if source.startswith(str(self.extracted))],
                         result.stderr)
        self.assertEqual({str(SNAPSERVER / repository) for repository in IMAGE_COPIES.values()},
                         {source for source in sources if source.startswith(str(SNAPSERVER))})

    def test_an_image_copy_that_differs_from_the_checkout_stops_before_any_install(self):
        unit = self.extracted / 'syren-nqptp.service'
        unit.write_text(unit.read_text().replace('User=syren-nqptp', 'User=root'))
        result, installs = self.run_installer()
        self.assertEqual(1, result.returncode)
        self.assertIn('syren-nqptp.service differs', result.stderr)
        self.assertEqual([], installs)


if __name__ == '__main__':
    unittest.main()
