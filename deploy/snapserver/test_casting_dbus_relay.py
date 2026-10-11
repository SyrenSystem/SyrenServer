import os
from pathlib import Path
import shlex
import socket
import struct
import tempfile
import threading
import unittest

from casting_dbus_relay import (MESSAGE_LIMIT, AvahiRelay, DbusMessageFilter, Denied, HandshakeError, LineReader,
                                check_avahi, check_denied, message_size, parse_message)

GUID = b'0123456789abcdef0123456789abcdef'
# The first bytes libdbus sent to a test socket, recorded with dbus-send 1.16 as uid 1000.
LIBDBUS_AUTH = b'\x00AUTH EXTERNAL 31303030\r\n'
LIBDBUS_BEGIN_AND_HELLO = (
    b'BEGIN\r\nl\x01\x00\x01\x00\x00\x00\x00\x01\x00\x00\x00n\x00\x00\x00\x01\x01o\x00\x15\x00\x00\x00'
    b'/org/freedesktop/DBus\x00\x00\x00\x06\x01s\x00\x14\x00\x00\x00org.freedesktop.DBus\x00\x00\x00\x00'
    b'\x02\x01s\x00\x14\x00\x00\x00org.freedesktop.DBus\x00\x00\x00\x00\x03\x01s\x00\x05\x00\x00\x00Hello'
    b'\x00\x00\x00')
HELLO = LIBDBUS_BEGIN_AND_HELLO[len(b'BEGIN\r\n'):]


def marshal(kind, serial, fields, body=b'', order='<', flags=0):
    encoded = b''
    for code, signature, value in fields:
        encoded += b'\0' * (-len(encoded) % 8) + bytes([code, len(signature)]) + signature + b'\0'
        if signature == b'u':
            encoded += b'\0' * (-len(encoded) % 4) + struct.pack(order + 'I', value)
        elif signature == b'g':
            encoded += bytes([len(value)]) + value + b'\0'
        else:
            text = value.encode()
            encoded += b'\0' * (-len(encoded) % 4) + struct.pack(order + 'I', len(text)) + text + b'\0'
    header = ((b'l' if order == '<' else b'B') + bytes([kind, flags, 1])
              + struct.pack(order + 'III', len(body), serial, len(encoded)) + encoded)
    return header + b'\0' * (-len(header) % 8) + body


def call(serial, destination, member, interface=None, path='/', order='<', signature=None, body=b''):
    fields = [(1, b'o', path)]
    if destination is not None:
        fields.append((6, b's', destination))
    if interface is not None:
        fields.append((2, b's', interface))
    fields.append((3, b's', member))
    if signature is not None:
        fields.append((8, b'g', signature))
    return marshal(1, serial, fields, body, order)


def text_body(text, order='<'):
    encoded = text.encode()
    return struct.pack(order + 'I', len(encoded)) + encoded + b'\0'


def method_return(serial, reply_serial, text=None):
    fields = [(5, b'u', reply_serial)]
    if text is None:
        return marshal(2, serial, fields)
    return marshal(2, serial, fields + [(8, b'g', b's')], text_body(text))


def split(buffer):
    messages = []
    while len(buffer) >= 16 and len(buffer) >= message_size(buffer):
        size = message_size(buffer)
        messages.append(bytes(buffer[:size]))
        del buffer[:size]
    return messages


class FakeDaemon:
    """A system bus that records what reaches it and answers every method call."""

    def __init__(self, path, version='avahi 0.8'):
        self.version = version
        self.connections = []
        self.server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.server.bind(path)
        self.server.listen(8)
        threading.Thread(target=self.serve, daemon=True).start()

    def close(self):
        try:
            self.server.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.server.close()
        for record in self.connections:
            record['socket'].close()

    def serve(self):
        while True:
            try:
                connection, _ = self.server.accept()
            except OSError:
                return
            record = {'socket': connection, 'auth': None, 'lines': [], 'messages': [], 'closed': threading.Event()}
            self.connections.append(record)
            threading.Thread(target=self.talk, args=(connection, record), daemon=True).start()

    def talk(self, connection, record):
        serial = 100
        try:
            connection.settimeout(5)
            reader = LineReader(connection)
            reader.nul()
            record['auth'] = reader.line()
            connection.sendall(b'OK ' + GUID + b'\r\n')
            record['lines'].append(reader.line())
            buffer = bytearray(reader.buffer)
            while True:
                for raw in split(buffer):
                    record['messages'].append(raw)
                    message = parse_message(raw)
                    member = message['fields'].get('member')
                    serial += 1
                    if member == 'GetVersionString':
                        connection.sendall(method_return(serial, message['serial'], self.version))
                    elif member == 'Hello':
                        connection.sendall(method_return(serial, message['serial'], ':1.7'))
                    else:
                        connection.sendall(method_return(serial, message['serial']))
                chunk = connection.recv(65536)
                if not chunk:
                    return
                buffer += chunk
        except (OSError, Denied, HandshakeError, ValueError):
            pass
        finally:
            record['closed'].set()


class RelayTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='relay-')
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.daemon = FakeDaemon(str(self.directory / 'bus'))
        self.addCleanup(self.daemon.close)
        self.logs = []
        self.relay = self.start_relay(os.getuid())

    def start_relay(self, allow_uid, name='relay'):
        relay = AvahiRelay(str(self.directory / name), str(self.directory / 'bus'), allow_uid,
                           log=self.logs.append).bind()
        worker = threading.Thread(target=relay.serve_forever, daemon=True)
        worker.start()
        self.addCleanup(worker.join, 2)
        self.addCleanup(relay.close)
        return relay

    def connect(self, relay=None):
        connection = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        connection.settimeout(3)
        connection.connect((relay or self.relay).listen_path)
        self.addCleanup(connection.close)
        return connection

    def receive_line(self, connection):
        data = b''
        while not data.endswith(b'\r\n'):
            chunk = connection.recv(1)
            if not chunk:
                return data
            data += chunk
        return data

    def receive_messages(self, connection, count):
        buffer = bytearray()
        messages = []
        while len(messages) < count:
            chunk = connection.recv(65536)
            if not chunk:
                break
            buffer += chunk
            messages += [parse_message(raw) for raw in split(buffer)]
        return messages

    def closed(self, connection):
        try:
            while True:
                if not connection.recv(65536):
                    return True
        except ConnectionResetError:
            return True
        except socket.timeout:
            return False

    def authenticated(self):
        connection = self.connect()
        connection.sendall(LIBDBUS_AUTH)
        self.assertEqual(b'OK ' + GUID + b'\r\n', self.receive_line(connection))
        connection.sendall(LIBDBUS_BEGIN_AND_HELLO)
        self.assertEqual(':1.7', self.version_of(self.receive_messages(connection, 1)[0]))
        return connection

    @staticmethod
    def version_of(message):
        return message['body'][4:-1].decode()

    def test_any_external_identity_is_accepted_from_the_allowed_uid_only(self):
        for opening, answer in [(LIBDBUS_AUTH, None), (b'\0AUTH EXTERNAL 313030\r\n', None),
                                (b'\0AUTH EXTERNAL\r\n', b'DATA\r\n')]:
            connection = self.connect()
            connection.sendall(opening)
            if answer:
                self.assertEqual(answer, self.receive_line(connection))
                connection.sendall(b'DATA\r\n')
            self.assertEqual(b'OK ' + GUID + b'\r\n', self.receive_line(connection))
        other = self.connect()
        other.sendall(b'\0AUTH ANONYMOUS\r\n')
        self.assertEqual(b'REJECTED EXTERNAL\r\n', self.receive_line(other))
        own_identity = b'AUTH EXTERNAL ' + str(os.getuid()).encode().hex().encode()
        self.assertEqual([own_identity] * 3, [record['auth'] for record in self.daemon.connections])
        stranger = self.start_relay(os.getuid() + 1, 'stranger')
        refused = self.connect(stranger)
        try:
            refused.sendall(LIBDBUS_AUTH)
        except BrokenPipeError:
            pass
        self.assertTrue(self.closed(refused))
        self.assertEqual(3, len(self.daemon.connections))
        self.assertTrue(any(line.startswith('Refused client') for line in self.logs))

    def test_unix_fd_negotiation_is_refused_locally(self):
        connection = self.connect()
        connection.sendall(LIBDBUS_AUTH)
        self.assertEqual(b'OK ' + GUID + b'\r\n', self.receive_line(connection))
        connection.sendall(b'NEGOTIATE_UNIX_FD\r\n')
        self.assertEqual(b'ERROR\r\n', self.receive_line(connection))
        connection.sendall(LIBDBUS_BEGIN_AND_HELLO)
        self.assertEqual(1, len(self.receive_messages(connection, 1)))
        self.assertEqual([b'BEGIN'], self.daemon.connections[0]['lines'])

    def test_avahi_calls_and_the_bus_basics_pass(self):
        connection = self.authenticated()
        calls = [
            call(2, 'org.freedesktop.DBus', 'AddMatch', 'org.freedesktop.DBus', '/org/freedesktop/DBus',
                 signature=b's', body=text_body("type='signal',sender='org.freedesktop.Avahi'")),
            call(3, 'org.freedesktop.DBus', 'RemoveMatch', 'org.freedesktop.DBus', '/org/freedesktop/DBus',
                 signature=b's', body=text_body("type='signal'")),
            call(4, 'org.freedesktop.DBus', 'GetNameOwner', 'org.freedesktop.DBus', '/org/freedesktop/DBus',
                 signature=b's', body=text_body('org.freedesktop.Avahi')),
            call(5, 'org.freedesktop.DBus', 'NameHasOwner', path='/org/freedesktop/DBus',
                 signature=b's', body=text_body('org.freedesktop.Avahi')),
            call(6, 'org.freedesktop.Avahi', 'GetAPIVersion', 'org.freedesktop.Avahi.Server'),
            call(7, 'org.freedesktop.Avahi', 'EntryGroupNew', 'org.freedesktop.Avahi.Server'),
            call(8, 'org.freedesktop.Avahi', 'Commit', 'org.freedesktop.Avahi.EntryGroup', '/Client1/EntryGroup1'),
            call(9, 'org.freedesktop.Avahi', 'GetVersionString', 'org.freedesktop.Avahi.Server'),
        ]
        connection.sendall(b''.join(calls))
        answers = self.receive_messages(connection, len(calls))
        self.assertEqual(list(range(2, 10)), [answer['fields']['reply_serial'] for answer in answers])
        self.assertEqual('avahi 0.8', self.version_of(answers[-1]))
        self.assertEqual([HELLO] + calls, self.daemon.connections[0]['messages'])
        self.assertFalse(any(line.startswith('Denied') for line in self.logs))

    def test_other_destinations_signals_and_set_host_name_close_the_connection(self):
        refused = [
            call(2, 'org.freedesktop.DBus', 'RequestName', 'org.freedesktop.DBus', '/org/freedesktop/DBus',
                 signature=b'su', body=text_body('org.example.Name') + b'\0' * 3 + struct.pack('<I', 4)),
            call(2, 'org.freedesktop.DBus', 'BecomeMonitor', 'org.freedesktop.DBus.Monitoring', '/org/freedesktop/DBus'),
            call(2, 'org.freedesktop.DBus', 'ListNames', 'org.freedesktop.DBus', '/org/freedesktop/DBus'),
            call(2, 'org.freedesktop.systemd1', 'StartUnit', 'org.freedesktop.systemd1.Manager', '/org/freedesktop/systemd1'),
            call(2, None, 'Introspect', 'org.freedesktop.DBus.Introspectable'),
            call(2, 'org.freedesktop.Avahi', 'SetHostName', 'org.freedesktop.Avahi.Server',
                 signature=b's', body=text_body('intruder')),
            call(2, 'org.freedesktop.Avahi', 'SetHostName', signature=b's', body=text_body('intruder')),
            marshal(4, 2, [(1, b'o', '/'), (2, b's', 'org.freedesktop.Avahi.Server'), (3, b's', 'StateChanged')]),
            method_return(2, 1),
        ]
        for index, message in enumerate(refused):
            connection = self.authenticated()
            connection.sendall(message)
            self.assertTrue(self.closed(connection), index)
            record = self.daemon.connections[index]
            self.assertTrue(record['closed'].wait(3), index)
            self.assertEqual([HELLO], record['messages'], index)
        denials = [line for line in self.logs if line.startswith('Denied')]
        self.assertEqual(len(refused), len(denials))
        self.assertIn("method_call to 'org.freedesktop.DBus' member 'RequestName'", denials[0])
        self.assertIn("signal to None member 'StateChanged'", denials[7])

    def test_messages_are_parsed_in_both_byte_orders_and_capped(self):
        for order in '<>':
            message = call(7, 'org.freedesktop.Avahi', 'EntryGroupNew', 'org.freedesktop.Avahi.Server',
                           order=order, signature=b's', body=text_body('x', order))
            parsed = parse_message(message)
            self.assertEqual({'path': '/', 'destination': 'org.freedesktop.Avahi', 'member': 'EntryGroupNew',
                              'interface': 'org.freedesktop.Avahi.Server', 'signature': 's'}, parsed['fields'])
            self.assertEqual(7, parsed['serial'])
            message_filter = DbusMessageFilter()
            passed = b''.join(message_filter.feed(message[index:index + 1]) for index in range(len(message)))
            self.assertEqual(message, passed)
        oversized = bytearray(call(2, 'org.freedesktop.Avahi', 'GetAPIVersion'))
        oversized[4:8] = struct.pack('<I', MESSAGE_LIMIT)
        with self.assertRaises(Denied):
            DbusMessageFilter().feed(bytes(oversized[:16]))
        unknown_field = marshal(1, 2, [(1, b'o', '/'), (6, b's', 'org.freedesktop.Avahi'), (3, b's', 'GetAPIVersion'),
                                       (9, b'u', 1)])
        twice = marshal(1, 2, [(1, b'o', '/'), (6, b's', 'org.freedesktop.Avahi'), (3, b's', 'GetAPIVersion'),
                               (6, b's', 'org.freedesktop.systemd1')])
        wrong_signature = marshal(1, 2, [(1, b'o', '/'), (6, b'o', '/org/freedesktop/Avahi'), (3, b's', 'GetAPIVersion')])
        bad_order = b'X' + call(2, 'org.freedesktop.Avahi', 'GetAPIVersion')[1:]
        dirty_padding = bytearray(call(2, 'org.freedesktop.Avahi', 'GetAPIVersion'))
        dirty_padding[-1] = 1
        for message in [unknown_field, twice, wrong_signature, bad_order, bytes(dirty_padding)]:
            with self.assertRaises(Denied):
                DbusMessageFilter().feed(message)

    def test_either_side_closing_closes_the_other(self):
        connection = self.authenticated()
        connection.close()
        self.assertTrue(self.daemon.connections[0]['closed'].wait(3))
        connection = self.authenticated()
        self.daemon.connections[1]['socket'].shutdown(socket.SHUT_RDWR)
        self.assertTrue(self.closed(connection))

    def test_check_reads_the_avahi_version(self):
        self.assertEqual('avahi 0.8', check_avahi(self.relay.listen_path))
        self.assertTrue(check_denied(self.relay.listen_path))
        self.assertFalse(check_denied(str(self.directory / 'bus')))
        self.assertIsNone(check_avahi(str(self.directory / 'missing')))
        self.daemon.version = 'something else'
        self.assertIsNone(check_avahi(self.relay.listen_path))

    def test_the_host_unit_matches_the_relay_options(self):
        host = Path(__file__).resolve().parent / 'host'
        unit = (host / 'syren-avahi-relay.service').read_text()
        command = shlex.split(next(line for line in unit.splitlines() if line.startswith('ExecStart=')).split('=', 1)[1])
        self.assertEqual(['/usr/bin/python3', '-I', '/usr/local/libexec/syrensystem/syren-avahi-relay.py',
                          '--listen', '/run/syren-avahi-relay/system_bus_socket',
                          '--upstream', '/run/dbus/system_bus_socket', '--allow-uid', '${SYREN_RELAY_ALLOW_UID}'], command)
        self.assertIn('d /run/syren-avahi-relay 0755 syren-avahi-relay syren-avahi-relay -',
                      (host / 'syrensystem-casting.tmpfiles.conf').read_text())
        self.assertIn('User=syren-avahi-relay', unit)
        self.assertIn('u syren-avahi-relay ', (host / 'syrensystem-casting.sysusers.conf').read_text())


if __name__ == '__main__':
    unittest.main()
