"""Plan stable Spotify receivers and preserve lifecycle events until acknowledged."""

from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import resource
import uuid


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex)
    descriptor = os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    try:
        with os.fdopen(descriptor, 'w') as stream:
            json.dump(value, stream)
            stream.flush()
            os.fsync(stream.fileno())
        temporary.replace(path)
        directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        temporary.unlink(missing_ok=True)


def read_json(path, default):
    return json.loads(path.read_text()) if path.exists() else default


# Real time priorities for the audio container, below the 88 PipeWire uses on the host.
SNAPSERVER_PRIORITY = 85
LIBRESPOT_PRIORITY = 84


def realtime_command(command, wanted):
    # Runs the command at real time priority when the container limit allows it, and normally otherwise.
    limit = resource.getrlimit(resource.RLIMIT_RTPRIO)[0]
    if os.geteuid() != 0 and limit != resource.RLIM_INFINITY:
        wanted = min(wanted, limit)
    return ['chrt', '--fifo', str(wanted), *command] if wanted > 0 else command

@dataclass(frozen=True)
class ReceiverSpec:
    identity: str
    profile: str | None
    account: str | None
    destination: str
    name: str
    guest: bool = False

    @property
    def stream(self):
        return 'spotify-' + self.identity


def receiver_identity(state_id, owner, destination):
    return hashlib.sha1(json.dumps([state_id, owner, destination], separators=(',', ':')).encode()).hexdigest()


def destinations(configuration):
    return [('house', 'SyrenSystem')] + [(group['id'], 'SyrenSystem · ' + group['name'])
        for group in configuration['groups'] if 'spotify' in group['enabledSources']]


def personal_receivers(configuration):
    return [ReceiverSpec(receiver_identity(configuration['stateId'], profile['id'], destination),
                         profile['id'], profile['spotifyAccountId'], destination, name)
            for profile in configuration['profiles'] if profile.get('spotifyAccountId')
            for destination, name in destinations(configuration)]


class LifecycleJournal:
    def __init__(self, path):
        self.transaction = False
        self.path = Path(path)
        self.state = read_json(self.path, {'receivers': {}, 'pending': []})

    def receiver(self, identity):
        return self.state['receivers'].setdefault(identity, {'sessionId': None, 'sequence': 0,
                                                            'accountId': None, 'commands': []})

    def emit(self, specification, action, account=None):
        receiver = self.receiver(specification.identity)
        if action == 'start':
            if receiver['sessionId'] is not None:
                return
            receiver['sessionId'] = uuid.uuid4().hex
            receiver['accountId'] = account or specification.account
        if receiver['sessionId'] is None:
            return
        receiver['sequence'] += 1
        event = {'sessionId': receiver['sessionId'], 'producerId': specification.identity,
                 'eventSequence': receiver['sequence'], 'action': action,
                 'source': 'spotify', 'ownerId': specification.profile, 'accountId': receiver['accountId'],
                 'destination': specification.destination,
                 'transports': [{'id': specification.stream, 'kind': 'snapcast', 'endpoint': specification.stream,
                                 'available': action not in ('interrupt', 'end')}]}
        self.state['pending'].append(event)
        if action == 'end':
            receiver['sessionId'] = None
        self.save()

    def save(self):
        if not self.transaction:
            atomic_json(self.path, self.state)

    def handle(self, specification, event):
        self.transaction = True
        try:
            self.handle_event(specification, event)
        finally:
            self.transaction = False
        self.save()

    def handle_event(self, specification, event):
        receiver = self.receiver(specification.identity)
        account = event.get('accountId') or receiver['accountId'] or specification.account
        if event['event'] == 'session_connected':
            if receiver['sessionId'] and receiver['accountId'] != account:
                self.emit(specification, 'end')
            self.emit(specification, 'start', account)
        elif event['event'] == 'syren_intent':
            command = event.get('commandId')
            if command and command in receiver['commands']:
                return
            action = event.get('action')
            if action not in ('play', 'pause', 'end', 'interrupt'):
                return
            if action in ('play', 'pause'):
                self.emit(specification, 'start', account)
            self.emit(specification, action, account)
            if command:
                receiver['commands'] = (receiver['commands'] + [command])[-1024:]
                self.save()
        elif event['event'] == 'playing':
            self.emit(specification, 'transport')
        elif event['event'] in ('loading', 'stopped', 'unavailable'):
            self.emit(specification, 'interrupt')

    def heartbeat(self, specification, generation):
        # Liveness repeats the latest sequence, so it is never journaled and never outruns an unsent event.
        receiver = self.receiver(specification.identity)
        session = receiver['sessionId']
        if session is None or any(event['sessionId'] == session for event in self.state['pending']):
            return None
        return {'sessionId': session, 'producerId': specification.identity, 'eventSequence': receiver['sequence'],
                'action': 'heartbeat', 'source': 'spotify', 'generation': generation}

    def abandon(self, session, producer):
        receiver = self.state['receivers'].get(producer)
        if receiver is None:
            return False
        pending = [event for event in self.state['pending'] if event['sessionId'] != session]
        changed = pending != self.state['pending'] or receiver['sessionId'] == session
        if receiver['sessionId'] == session:
            receiver['sessionId'] = None
        self.state['pending'] = pending
        if changed:
            self.save()
        return changed

    def forget(self, identities):
        unused = [identity for identity, receiver in self.state['receivers'].items()
                  if identity not in identities and receiver['sessionId'] is None and
                  not any(event['producerId'] == identity for event in self.state['pending'])]
        for identity in unused:
            del self.state['receivers'][identity]
        if unused:
            self.save()

    def reset(self):
        self.state = {'receivers': {}, 'pending': []}
        self.save()

    def acknowledge(self, catalogue):
        acknowledged = {session['id']: session['eventSequence'] for session in catalogue['sessions']}
        ended = {session['id'] for session in catalogue['sessions'] if session.get('state') == 'ended'}
        changed = False
        for receiver in self.state['receivers'].values():
            if receiver['sessionId'] in ended:
                receiver['sessionId'] = None
                changed = True
        pending = [event for event in self.state['pending'] if event['sessionId'] not in ended and
                   event['eventSequence'] > acknowledged.get(event['sessionId'], 0)]
        if changed or pending != self.state['pending']:
            self.state['pending'] = pending
            self.save()

    def events(self, generation):
        return [dict(event, generation=generation) for event in self.state['pending']]
