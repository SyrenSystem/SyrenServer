#!/usr/bin/env python3
"""Check the current Snapserver protocol with private synthetic sources."""

import argparse
import json
import math
from pathlib import Path
import socket
import struct
import subprocess
import tempfile
import threading
import time
import urllib.request


def private_port():
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1', 0))
        return reservation.getsockname()[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--executable', default='/usr/bin/snapserver')
    arguments = parser.parse_args()
    ports = {name: private_port() for name in ['http', 'stream', 'spotify', 'laptop']}
    stopped = threading.Event()
    enabled = {name: threading.Event() for name in ['spotify', 'laptop']}
    senders = []
    failures = []
    with tempfile.TemporaryDirectory(prefix='syren-snapserver-contract-') as temporary:
        directory = Path(temporary)
        configuration = directory / 'snapserver.conf'
        configuration.write_text(f'''[http]
enabled = true
bind_to_address = 127.0.0.1
port = {ports['http']}
[tcp]
enabled = false
[stream]
bind_to_address = 127.0.0.1
port = {ports['stream']}
source = tcp://127.0.0.1:{ports['spotify']}?name=spotify&mode=server&sampleformat=48000:16:2&idle_threshold=100&codec=pcm
source = tcp://127.0.0.1:{ports['laptop']}?name=laptop&mode=server&sampleformat=48000:16:2&idle_threshold=100&codec=pcm
''')
        with (directory / 'server.log').open('w') as log:
            process = subprocess.Popen([arguments.executable, '--config', str(configuration),
                                        f'--server.datadir={directory}'], stdout=log, stderr=log)

        def request(method, parameters=None):
            payload = json.dumps({'id': 1, 'jsonrpc': '2.0', 'method': method,
                                  'params': parameters or {}}).encode()
            operation = urllib.request.Request(f'http://127.0.0.1:{ports["http"]}/jsonrpc',
                data=payload, headers={'Content-Type': 'application/json'})
            result = json.load(urllib.request.urlopen(operation, timeout=2))
            assert 'error' not in result, result
            return result['result']

        def states():
            return {stream['id']: stream['status'] for stream in request('Server.GetStatus')['server']['streams']}

        def wait_for(expected):
            expires = time.monotonic() + 4
            while time.monotonic() < expires:
                if process.poll() is not None:
                    raise AssertionError((directory / 'server.log').read_text())
                try:
                    current = states()
                    if all(current.get(name) == state for name, state in expected.items()):
                        return
                except OSError:
                    pass
                time.sleep(.02)
            raise AssertionError(f'Stream states did not converge: {expected}, actual: {states()}')

        def transmit(name, frequency):
            tone = b''.join(struct.pack('<hh', *([int(2000 * math.sin(index * 2 * math.pi * frequency / 48000))] * 2))
                            for index in range(960))
            try:
                with socket.create_connection(('127.0.0.1', ports[name]), timeout=2) as connection:
                    while not stopped.is_set():
                        connection.sendall(tone if enabled[name].is_set() else bytes(len(tone)))
                        stopped.wait(.02)
            except OSError as error:
                if not stopped.is_set():
                    failures.append(str(error))

        try:
            wait_for({})
            for name, frequency in [('spotify', 400), ('laptop', 1000)]:
                sender = threading.Thread(target=transmit, args=(name, frequency))
                sender.start()
                senders.append(sender)
            request('Stream.AddStream', {'streamUri': 'meta:///spotify/laptop?name=priority&codec=pcm&sampleformat=48000:16:2'})
            for spotify, laptop in [(False, True), (True, True), (False, True), (False, False)] * 3:
                for name, active in [('spotify', spotify), ('laptop', laptop)]:
                    enabled[name].set() if active else enabled[name].clear()
                wait_for({'spotify': 'playing' if spotify else 'idle',
                          'laptop': 'playing' if laptop else 'idle',
                          'priority': 'playing' if spotify or laptop else 'idle'})
            request('Stream.RemoveStream', {'id': 'priority'})
            assert 'priority' not in states()
            for cycle in range(30):
                enabled['spotify'].set()
                wait_for({'spotify': 'playing'})
                request('Stream.AddStream', {'streamUri': 'meta:///spotify/laptop?name=priority&codec=pcm&sampleformat=48000:16:2'})
                enabled['spotify'].clear()
                enabled['laptop'].set()
                wait_for({'spotify': 'idle', 'laptop': 'playing', 'priority': 'playing'})
                request('Stream.RemoveStream', {'id': 'priority'})
                enabled['laptop'].clear()
                wait_for({'laptop': 'idle'})
                assert 'priority' not in states()
            assert not failures, failures
            print(json.dumps({'transitions': 12, 'source_events': 'passed',
                              'priority_stream_lifecycle': 'passed', 'stream_removals': 31,
                              'server_pid': process.pid}))
        except Exception:
            print((directory / 'server.log').read_text())
            raise
        finally:
            stopped.set()
            for sender in senders:
                sender.join(timeout=3)
            process.terminate()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=3)


if __name__ == '__main__':
    main()
