#!/usr/bin/python3
"""Run Snapserver with its Spotify and casting supervisors as one service."""

import os
from pathlib import Path
import signal
import subprocess
import time

from spotify_sessions import SNAPSERVER_PRIORITY, realtime_command
from spotify_supervisor import stop

CASTING_COMMAND = ['/usr/bin/python3', '/usr/local/bin/casting_supervisor.py']
CASTING_DELAY_LIMIT = 30
CASTING_STABLE_SECONDS = 60
CASTING_STOP_SECONDS = 4


def write_configuration():
    configuration = Path('/etc/snapserver.conf').read_text()
    configuration = '\n'.join(line for line in configuration.splitlines() if not line.startswith('source ='))
    Path('/tmp/snapserver-profiles.conf').write_text(configuration + '\n')


def reap_orphans(children):
    # Instances of a crashed casting supervisor end up here, so collect every exited child that is not ours.
    while True:
        try:
            result = os.waitid(os.P_ALL, 0, os.WEXITED | os.WNOHANG | os.WNOWAIT)
        except ChildProcessError:
            return
        if result is None or result.si_pid in children:
            return
        os.waitpid(result.si_pid, 0)


def main():
    write_configuration()
    server = subprocess.Popen(realtime_command(['/usr/bin/snapserver', '--config', '/tmp/snapserver-profiles.conf',
                                                '--server.datadir=/var/lib/snapserver'], SNAPSERVER_PRIORITY))
    supervisor = subprocess.Popen(['/usr/bin/python3', '/usr/local/bin/spotify_supervisor.py'])
    casting_enabled = os.environ.get('SYREN_CASTING') == '1'
    casting = subprocess.Popen(CASTING_COMMAND) if casting_enabled else None
    casting_started = time.monotonic()
    casting_delay = 1
    casting_restart = None
    stopping = False

    def shutdown(*arguments):
        nonlocal stopping
        stopping = True

    for signal_number in (signal.SIGINT, signal.SIGTERM):
        signal.signal(signal_number, shutdown)
    try:
        while not stopping and server.poll() is None and supervisor.poll() is None:
            if casting is not None and casting.poll() is not None:
                # A casting fault never stops Spotify, so only this child restarts.
                if time.monotonic() - casting_started >= CASTING_STABLE_SECONDS:
                    casting_delay = 1
                print(f'Casting supervisor exited with {casting.returncode}; restarting in {casting_delay} s', flush=True)
                casting_restart = time.monotonic() + casting_delay
                casting_delay = min(casting_delay * 2, CASTING_DELAY_LIMIT)
                casting = None
            elif casting_enabled and casting is None and time.monotonic() >= casting_restart:
                casting = subprocess.Popen(CASTING_COMMAND)
                casting_started = time.monotonic()
            if casting_enabled:
                reap_orphans({child.pid for child in (server, supervisor, casting) if child is not None})
            time.sleep(0.1)
    finally:
        # Spotify still gets SIGTERM at the same moment, and Snapserver still stops last.
        casting_deadline = time.monotonic() + CASTING_STOP_SECONDS
        if casting is not None and casting.poll() is None:
            casting.terminate()
        stop(supervisor)
        stop(casting, timeout=max(0, casting_deadline - time.monotonic()))
        stop(server)
    return 0 if stopping else 1


if __name__ == '__main__':
    raise SystemExit(main())
