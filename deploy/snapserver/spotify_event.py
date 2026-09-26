#!/usr/bin/python3
"""Forward only playback events to the local receiver supervisor."""

import json
import os
import socket


def main():
    event = os.environ.get('PLAYER_EVENT')
    if event not in ('syren_intent', 'session_connected', 'playing', 'loading', 'stopped', 'unavailable'):
        return
    message = {
        'receiverId': os.environ['SYREN_RECEIVER_ID'],
        'instanceId': os.environ['SYREN_RECEIVER_INSTANCE'],
        'event': event,
        'action': os.environ.get('ACTION'),
        'accountId': os.environ.get('USER_NAME'),
        'commandId': os.environ.get('COMMAND_ID'),
    }
    with socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM) as connection:
        connection.settimeout(2)
        connection.sendto(json.dumps(message).encode(), os.environ['SYREN_EVENT_SOCKET'])


if __name__ == '__main__':
    main()
