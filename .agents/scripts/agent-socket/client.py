#!/usr/bin/env python3
"""Stdlib-only client for the OpenRA agent debug socket (Debug.AgentSocket=true).

Run isolated so nothing in the script's own directory shadows stdlib modules:

    python -I client.py [--host H] [--port P] [--timeout S] send <json> [<json> ...]
    python -I client.py [--host H] [--port P] [--timeout S] raw <text>

send: each argument is one JSON object; prints its response line. Exit 0 only if every response has ok:true.
raw:  sends the text verbatim plus a newline; prints the next response line. Exit 0 if one arrived.
"""

import argparse
import json
import socket
import sys


def read_line(f):
    line = f.readline()
    if not line:
        raise ConnectionError("connection closed by the game")
    return line.decode("utf-8")


def read_response(f):
    # Event lines carry "event" and no "ok"; responses always carry "ok". M1 has no events, but skip them anyway.
    while True:
        line = read_line(f)
        try:
            obj = json.loads(line)
        except ValueError:
            return line
        if isinstance(obj, dict) and "ok" in obj:
            return line


def connect(args):
    sock = socket.create_connection((args.host, args.port), timeout=args.timeout)
    f = sock.makefile("rwb")
    # The banner is one or more text lines terminated by a blank line.
    while read_line(f).strip():
        pass
    return sock, f


def send_line(f, text):
    f.write(text.encode("utf-8") + b"\n")
    f.flush()


def cmd_send(args):
    sock, f = connect(args)
    with sock:
        ok = True
        for text in args.json:
            send_line(f, text)
            line = read_response(f)
            sys.stdout.write(line)
            try:
                ok = ok and json.loads(line).get("ok") is True
            except ValueError:
                ok = False
        return 0 if ok else 1


def cmd_raw(args):
    sock, f = connect(args)
    with sock:
        send_line(f, args.text)
        sys.stdout.write(read_response(f))
        return 0


def main(argv):
    parser = argparse.ArgumentParser(description="OpenRA agent debug socket client")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=37655)
    parser.add_argument("--timeout", type=float, default=10.0)
    sub = parser.add_subparsers(dest="command", required=True)

    p_send = sub.add_parser("send", help="send JSON objects and print their responses")
    p_send.add_argument("json", nargs="+")
    p_send.set_defaults(func=cmd_send)

    p_raw = sub.add_parser("raw", help="send text verbatim and print the next response")
    p_raw.add_argument("text")
    p_raw.set_defaults(func=cmd_raw)

    args = parser.parse_args(argv)
    try:
        return args.func(args)
    except (ConnectionError, OSError) as ex:
        print(f"agent-socket: {ex}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
