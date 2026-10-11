#!/usr/bin/python3
"""Let the audio container reach the host Avahi daemon over the system bus, and nothing else there."""

import argparse
import os
import selectors
import socket
import stat
import struct
import sys
import threading

SYSTEM_BUS = '/run/dbus/system_bus_socket'
MESSAGE_LIMIT = 1024 * 1024
HANDSHAKE_SECONDS = 5
SEND_SECONDS = 30
HANDSHAKE_LINES = 16
LINE_LIMIT = 1024
CLIENT_LIMIT = 64
AVAHI = 'org.freedesktop.Avahi'
BUS = 'org.freedesktop.DBus'
# Every call avahi-client makes to the bus itself.
BUS_MEMBERS = frozenset({'Hello', 'AddMatch', 'RemoveMatch', 'GetNameOwner', 'NameHasOwner'})
METHOD_CALL, METHOD_RETURN, ERROR, SIGNAL = 1, 2, 3, 4
MESSAGE_TYPES = {METHOD_CALL: 'method_call', METHOD_RETURN: 'method_return', ERROR: 'error', SIGNAL: 'signal'}
# Header fields by code, with the one signature the bus specification gives each; unix fds are never accepted.
HEADER_FIELDS = {1: ('path', b'o'), 2: ('interface', b's'), 3: ('member', b's'), 4: ('error_name', b's'),
                 5: ('reply_serial', b'u'), 6: ('destination', b's'), 7: ('sender', b's'), 8: ('signature', b'g')}


class Denied(Exception):
    pass


class HandshakeError(Exception):
    pass


def align(offset, alignment):
    return offset + (-offset) % alignment


def message_size(data, limit=MESSAGE_LIMIT):
    # Reads the fixed 16 byte header, so an oversized message is refused before its body arrives.
    if data[0:1] not in (b'l', b'B') or data[3] != 1 or data[1] not in MESSAGE_TYPES:
        raise Denied('malformed header')
    body_length, _, fields_length = struct.unpack(('<' if data[0:1] == b'l' else '>') + 'III', data[4:16])
    size = align(16 + fields_length, 8) + body_length
    if size > limit:
        raise Denied(f'message of {size} bytes')
    return size


def parse_message(data):
    order = '<' if data[0:1] == b'l' else '>'
    kind, flags = data[1], data[2]
    body_length, serial, fields_length = struct.unpack(order + 'III', data[4:16])
    end = 16 + fields_length
    body_start = align(end, 8)
    if serial == 0 or len(data) != body_start + body_length or any(data[end:body_start]):
        raise Denied('malformed header')
    fields = {}
    offset = 16
    try:
        while offset < end:
            start = align(offset, 8)
            if any(data[offset:start]):
                raise Denied('malformed header')
            code, signature_length = data[start], data[start + 1]
            signature = data[start + 2:start + 2 + signature_length]
            offset = start + 3 + signature_length
            known = HEADER_FIELDS.get(code)
            if not known or signature != known[1] or data[offset - 1] != 0 or known[0] in fields:
                raise Denied('malformed header')
            if signature == b'u':
                value_start = align(offset, 4)
                if any(data[offset:value_start]):
                    raise Denied('malformed header')
                fields[known[0]] = struct.unpack(order + 'I', data[value_start:value_start + 4])[0]
                offset = value_start + 4
            else:
                if signature == b'g':
                    length_start, length = offset, data[offset]
                    value_start = offset + 1
                else:
                    length_start = align(offset, 4)
                    if any(data[offset:length_start]):
                        raise Denied('malformed header')
                    length = struct.unpack(order + 'I', data[length_start:length_start + 4])[0]
                    value_start = length_start + 4
                value = data[value_start:value_start + length]
                if len(value) != length or data[value_start + length] != 0 or b'\0' in value:
                    raise Denied('malformed header')
                fields[known[0]] = value.decode('utf-8')
                offset = value_start + length + 1
            if offset > end:
                raise Denied('malformed header')
    except (IndexError, struct.error, UnicodeDecodeError):
        raise Denied('malformed header') from None
    return {'type': kind, 'flags': flags, 'serial': serial, 'fields': fields, 'order': order,
            'body': data[body_start:]}


def describe(message):
    fields = message['fields']
    return (f"{MESSAGE_TYPES.get(message['type'], message['type'])} to {ascii(fields.get('destination'))[:120]} "
            f"member {ascii(fields.get('member'))[:120]}")


def allowed(message):
    fields = message['fields']
    if message['type'] != METHOD_CALL or 'path' not in fields or 'member' not in fields:
        return False
    destination, interface, member = fields.get('destination'), fields.get('interface'), fields['member']
    if destination == AVAHI:
        return member != 'SetHostName'
    return destination == BUS and interface in (None, BUS) and member in BUS_MEMBERS


class DbusMessageFilter:
    """Split the container's byte stream into bus messages and keep only the calls avahi-client makes."""

    def __init__(self, limit=MESSAGE_LIMIT):
        self.limit = limit
        self.buffer = bytearray()

    def feed(self, data):
        # Returns the complete allowed messages, and raises Denied on anything else.
        self.buffer += data
        passed = bytearray()
        while len(self.buffer) >= 16:
            size = message_size(self.buffer, self.limit)
            if len(self.buffer) < size:
                break
            message = parse_message(bytes(self.buffer[:size]))
            if not allowed(message):
                raise Denied(describe(message))
            passed += self.buffer[:size]
            del self.buffer[:size]
        return bytes(passed)


class LineReader:
    def __init__(self, connection):
        self.connection = connection
        self.buffer = bytearray()

    def fill(self):
        chunk = self.connection.recv(4096)
        if not chunk:
            raise HandshakeError('connection closed during the handshake')
        self.buffer += chunk

    def nul(self):
        if not self.buffer:
            self.fill()
        if self.buffer[0] != 0:
            raise HandshakeError('missing leading NUL byte')
        del self.buffer[0]

    def line(self):
        while b'\r\n' not in self.buffer:
            if len(self.buffer) > LINE_LIMIT:
                raise HandshakeError('handshake line too long')
            self.fill()
        line, _, rest = bytes(self.buffer).partition(b'\r\n')
        self.buffer = bytearray(rest)
        return line


def login(upstream):
    # Logs in to the real bus as this process, and returns the bus GUID.
    upstream.sendall(b'\0AUTH EXTERNAL ' + str(os.getuid()).encode().hex().encode() + b'\r\n')
    reader = LineReader(upstream)
    words = reader.line().split(b' ')
    if len(words) != 2 or words[0] != b'OK' or len(words[1]) != 32:
        raise HandshakeError('the system bus refused the relay')
    return words[1], reader


def serve_handshake(reader, send, authenticate):
    # Plays the SASL server for libdbus, accepting EXTERNAL with any identity, and returns the bytes after BEGIN.
    reader.nul()
    waiting_for_data = False
    for _ in range(HANDSHAKE_LINES):
        words = reader.line().split(b' ')
        command = words[0]
        if command == b'AUTH' and words[1:2] == [b'EXTERNAL'] and len(words) <= 3 and not waiting_for_data:
            if len(words) == 3:
                break
            send(b'DATA\r\n')
            waiting_for_data = True
        elif command == b'DATA' and len(words) <= 2 and waiting_for_data:
            break
        elif command in (b'AUTH', b'CANCEL', b'ERROR'):
            send(b'REJECTED EXTERNAL\r\n')
            waiting_for_data = False
        elif command == b'BEGIN':
            raise HandshakeError('BEGIN before OK')
        else:
            send(b'ERROR\r\n')
    else:
        raise HandshakeError('authentication did not finish')
    send(b'OK ' + authenticate() + b'\r\n')
    for _ in range(HANDSHAKE_LINES):
        line = reader.line()
        if line == b'BEGIN':
            return bytes(reader.buffer)
        if line != b'NEGOTIATE_UNIX_FD':
            raise HandshakeError('unexpected command after OK')
        # Avahi never passes file descriptors.
        send(b'ERROR\r\n')
    raise HandshakeError('BEGIN never came')


def peer_uid(connection):
    _, uid, _ = struct.unpack('3i', connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize('3i')))
    return uid


def close(connection):
    try:
        connection.shutdown(socket.SHUT_RDWR)
    except OSError:
        pass
    connection.close()


class AvahiRelay:
    """Accept the audio container on its own socket and forward only Avahi traffic to the system bus."""

    def __init__(self, listen_path, upstream_path, allow_uid, log=None):
        self.listen_path = listen_path
        self.upstream_path = upstream_path
        self.allow_uid = allow_uid
        self.log = log or (lambda line: print(line, file=sys.stderr, flush=True))
        self.server = None
        self.clients = threading.BoundedSemaphore(CLIENT_LIMIT)
        self.sequence = 0

    def bind(self):
        # A socket left by an earlier run is replaced; any other file there stops the start.
        try:
            if stat.S_ISSOCK(os.lstat(self.listen_path).st_mode):
                os.unlink(self.listen_path)
        except FileNotFoundError:
            pass
        self.server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.server.bind(self.listen_path)
        os.chmod(self.listen_path, 0o666)
        self.server.listen(16)
        return self

    def close(self):
        server, self.server = self.server, None
        if server is not None:
            close(server)

    def serve_forever(self):
        while self.server is not None:
            try:
                client, _ = self.server.accept()
            except OSError:
                if self.server is None:
                    return
                raise
            self.accept(client)

    def accept(self, client):
        self.sequence += 1
        number = self.sequence
        try:
            uid = peer_uid(client)
        except OSError:
            uid = None
        if uid != self.allow_uid:
            self.log(f'Refused client {number} with uid {uid}')
            close(client)
            return None
        if not self.clients.acquire(blocking=False):
            self.log(f'Refused client {number}: more than {CLIENT_LIMIT} clients')
            close(client)
            return None
        worker = threading.Thread(target=self.handle, args=(client, number), daemon=True)
        worker.start()
        return worker

    def handle(self, client, number):
        upstream = None
        upstream_reader = None
        try:
            client.settimeout(HANDSHAKE_SECONDS)
            upstream = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            upstream.settimeout(HANDSHAKE_SECONDS)

            def authenticate():
                # The relay logs in as itself only once the container has authenticated.
                nonlocal upstream_reader
                upstream.connect(self.upstream_path)
                guid, upstream_reader = login(upstream)
                return guid

            leftover = serve_handshake(LineReader(client), client.sendall, authenticate)
            upstream.sendall(b'BEGIN\r\n')
            client.settimeout(SEND_SECONDS)
            upstream.settimeout(SEND_SECONDS)
            if upstream_reader.buffer:
                client.sendall(bytes(upstream_reader.buffer))
            self.pump(client, upstream, DbusMessageFilter(), leftover)
        except Denied as denial:
            self.log(f'Denied client {number}: {denial}')
        except (HandshakeError, OSError) as error:
            self.log(f'Closed client {number}: {error}')
        finally:
            close(client)
            if upstream is not None:
                close(upstream)
            self.clients.release()

    @staticmethod
    def pump(client, upstream, message_filter, leftover):
        # Either side closing ends both, so Avahi drops an instance's records as soon as it exits.
        if leftover:
            upstream.sendall(message_filter.feed(leftover))
        with selectors.DefaultSelector() as selector:
            selector.register(client, selectors.EVENT_READ)
            selector.register(upstream, selectors.EVENT_READ)
            while True:
                for key, _ in selector.select():
                    data = key.fileobj.recv(65536)
                    if not data:
                        return
                    if key.fileobj is client:
                        upstream.sendall(message_filter.feed(data))
                    else:
                        client.sendall(data)


def method_call(serial, path, destination, interface, member, signature=b'', body=b''):
    fields = b''
    values = [(1, b'o', path), (6, b's', destination), (2, b's', interface), (3, b's', member)]
    if signature:
        values.append((8, b'g', signature))
    for code, field_signature, value in values:
        fields += b'\0' * ((-len(fields)) % 8) + bytes([code, len(field_signature)]) + field_signature + b'\0'
        if field_signature == b'g':
            fields += bytes([len(value)]) + value + b'\0'
        else:
            fields += b'\0' * ((-len(fields)) % 4)
            encoded = value.encode()
            fields += struct.pack('<I', len(encoded)) + encoded + b'\0'
    header = b'l' + bytes([METHOD_CALL, 0, 1]) + struct.pack('<III', len(body), serial, len(fields)) + fields
    return header + b'\0' * ((-len(header)) % 8) + body


def string_body(text):
    encoded = text.encode()
    return struct.pack('<I', len(encoded)) + encoded + b'\0'


def client_session(socket_path, timeout):
    connection = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try:
        connection.settimeout(timeout)
        connection.connect(socket_path)
        reader = LineReader(connection)
        connection.sendall(b'\0AUTH EXTERNAL\r\n')
        if reader.line() != b'DATA':
            raise HandshakeError('no DATA challenge')
        connection.sendall(b'DATA\r\n')
        if not reader.line().startswith(b'OK '):
            raise HandshakeError('authentication refused')
        connection.sendall(b'BEGIN\r\n' + method_call(1, '/org/freedesktop/DBus', BUS, BUS, 'Hello'))
    except BaseException:
        connection.close()
        raise
    return connection, bytearray(reader.buffer)


def replies(connection, buffer):
    # Yields every message the bus sends until the connection closes.
    while True:
        while len(buffer) >= 16:
            size = message_size(buffer)
            if len(buffer) < size:
                break
            yield parse_message(bytes(buffer[:size]))
            del buffer[:size]
        chunk = connection.recv(65536)
        if not chunk:
            return
        buffer += chunk


def check_avahi(socket_path, timeout=2.0):
    # Returns the Avahi version string through this socket, or None on any failure.
    connection = None
    try:
        connection, buffer = client_session(socket_path, timeout)
        connection.sendall(method_call(2, '/', AVAHI, AVAHI + '.Server', 'GetVersionString'))
        for message in replies(connection, buffer):
            if message['fields'].get('reply_serial') != 2:
                continue
            body = message['body']
            if message['type'] != METHOD_RETURN or message['fields'].get('signature') != 's' or len(body) < 5:
                return None
            length = struct.unpack(message['order'] + 'I', body[:4])[0]
            version = body[4:4 + length].decode('utf-8')
            return version if 'avahi' in version else None
        return None
    except Exception:
        # The supervisor calls this in its loop, so any failure only means no Avahi.
        return None
    finally:
        if connection is not None:
            connection.close()


def check_denied(socket_path, timeout=2.0):
    # True only when Hello passes and the relay then closes the connection on RequestName.
    connection = None
    hello_answered = False
    try:
        connection, buffer = client_session(socket_path, timeout)
        messages = replies(connection, buffer)
        for message in messages:
            if message['fields'].get('reply_serial') == 1:
                hello_answered = message['type'] == METHOD_RETURN
                break
        if not hello_answered:
            return False
        body = string_body('org.syrensystem.RelayCheck')
        body += b'\0' * ((-len(body)) % 4) + struct.pack('<I', 4)
        connection.sendall(method_call(2, '/org/freedesktop/DBus', BUS, BUS, 'RequestName', b'su', body))
        for message in messages:
            if message['fields'].get('reply_serial') == 2:
                return False
        return True
    except (ConnectionResetError, BrokenPipeError):
        return hello_answered
    except Exception:
        return False
    finally:
        if connection is not None:
            connection.close()


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--listen', metavar='SOCKET', help='serve the audio container on this socket')
    mode.add_argument('--check', metavar='SOCKET', help='print the Avahi version read through this socket')
    mode.add_argument('--check-denied', metavar='SOCKET', help='require that RequestName closes the connection')
    parser.add_argument('--upstream', default=SYSTEM_BUS)
    parser.add_argument('--allow-uid', type=int)
    options = parser.parse_args(arguments)
    if options.check:
        version = check_avahi(options.check)
        if version is None:
            print(f'Avahi did not answer through {options.check}', file=sys.stderr)
            return 1
        print(version)
        return 0
    if options.check_denied:
        if not check_denied(options.check_denied):
            print(f'The relay at {options.check_denied} did not refuse RequestName', file=sys.stderr)
            return 1
        print('The relay closed the connection after RequestName')
        return 0
    if options.allow_uid is None:
        parser.error('--listen needs --allow-uid')
    relay = AvahiRelay(options.listen, options.upstream, options.allow_uid).bind()
    relay.log(f'Relaying Avahi calls from uid {options.allow_uid} on {options.listen} to {options.upstream}')
    try:
        relay.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        relay.close()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
