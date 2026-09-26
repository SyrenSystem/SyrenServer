#!/usr/bin/python3
"""Run Snapserver and its Spotify supervisor as one service."""

import os
from pathlib import Path
import signal
import subprocess
import time

from spotify_supervisor import stop


def main():
    configuration = Path('/etc/snapserver.conf').read_text()
    configuration = '\n'.join(line for line in configuration.splitlines() if not line.startswith('source ='))
    Path('/tmp/snapserver-profiles.conf').write_text(configuration + '\n')
    server = subprocess.Popen(['/usr/bin/snapserver', '--config', '/tmp/snapserver-profiles.conf',
                               '--server.datadir=/var/lib/snapserver'])
    supervisor = subprocess.Popen(['/usr/bin/python3', '/usr/local/bin/spotify_supervisor.py'])
    stopping = False

    def shutdown(*arguments):
        nonlocal stopping
        stopping = True

    for signal_number in (signal.SIGINT, signal.SIGTERM):
        signal.signal(signal_number, shutdown)
    try:
        while not stopping and server.poll() is None and supervisor.poll() is None:
            time.sleep(0.1)
    finally:
        stop(supervisor)
        stop(server)
    return 0 if stopping else 1


if __name__ == '__main__':
    raise SystemExit(main())
