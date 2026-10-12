#!/usr/bin/env python3
"""Stdlib-only client for the OpenRA agent debug socket (Debug.AgentSocket=true).

Run isolated so nothing in the script's own directory shadows stdlib modules:

    python -I client.py [--host H] [--port P] [--timeout S] send <json> [<json> ...]
    python -I client.py [--host H] [--port P] [--timeout S] raw <text>
    python -I client.py [--host H] [--port P] [--timeout S] listen [--subscribe G1,G2] --seconds N --out FILE
                        [--interleave JSON --every S]

send: each argument is one JSON object; prints its response line. Exit 0 only if every response has ok:true.
raw:  sends the text verbatim plus a newline; prints the next response line. Exit 0 if one arrived.
listen: subscribes to event groups, writes each event line to FILE, and optionally sends a command every S seconds
      (--interleave). Prints event counts by event name and responses=/missing=. Exit 0 only if the subscribe reply
      and every interleaved response are ok, and none is missing.
"""

import argparse
import json
import socket
import sys
import time
from collections import Counter


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


def next_line(sock, buf):
    """Returns the next line, or None if the socket timed out first. Reads the raw socket: a makefile object cannot resume after a timeout."""
    while True:
        end = buf.find(10)
        if end >= 0:
            line = bytes(buf[:end + 1])
            del buf[:end + 1]
            return line.decode("utf-8")
        try:
            chunk = sock.recv(65536)
        except socket.timeout:
            return None
        if not chunk:
            raise ConnectionError("connection closed by the game")
        buf.extend(chunk)


class Listener:
    """Routes each line read by listen: event lines to the file and counts, response lines to the subscribe or interleave tally."""

    def __init__(self, out):
        self.out = out
        self.counts = Counter()
        self.dropped = 0
        self.subscribed = None
        self.subscribe_ok = None
        self.sent = 0
        self.responses = 0
        self.failed = False

    def handle(self, line, subscribing):
        try:
            obj = json.loads(line)
        except ValueError:
            self.failed = True
            return
        if not isinstance(obj, dict):
            self.failed = True
            return
        if "event" in obj:
            self.out.write(line if line.endswith("\n") else line + "\n")
            self.counts[obj["event"]] += 1
            if obj["event"] == "overflow":
                self.dropped += obj.get("dropped", 0)
        elif "ok" in obj:
            if subscribing and obj.get("cmd") == "subscribe":
                subscribing = False
                self.subscribe_ok = obj["ok"] is True
                self.subscribed = obj.get("subscribed")
                self.failed = self.failed or not self.subscribe_ok
            else:
                self.responses += 1
                self.failed = self.failed or obj["ok"] is not True
            sys.stdout.write(line)
        return subscribing


def cmd_listen(args):
    groups = [g.strip() for g in (args.subscribe or "").split(",") if g.strip()]
    sock, f = connect(args)
    with sock, open(args.out, "w", encoding="utf-8", newline="\n") as out:
        sock.settimeout(0.2)
        buf = bytearray()
        listener = Listener(out)
        if groups:
            send_line(f, json.dumps({"cmd": "subscribe", "events": groups}))
        subscribing = bool(groups)

        start = time.monotonic()
        end = start + args.seconds
        next_send = start + args.every if args.interleave else None
        while time.monotonic() < end:
            if next_send is not None and time.monotonic() >= next_send:
                send_line(f, args.interleave)
                listener.sent += 1
                next_send += args.every
            line = next_line(sock, buf)
            if line is not None:
                subscribing = listener.handle(line, subscribing)

        # Give the last interleaved commands up to --timeout to answer.
        grace = time.monotonic() + args.timeout
        while listener.responses < listener.sent and time.monotonic() < grace:
            line = next_line(sock, buf)
            if line is not None:
                subscribing = listener.handle(line, subscribing)

        missing = listener.sent - listener.responses
        counts = " ".join(f"{name}={n}" for name, n in sorted(listener.counts.items())) or "none"
        print(f"events {counts}")
        print(f"overflow dropped={listener.dropped}")
        print(f"subscribed={json.dumps(listener.subscribed)}")
        print(f"responses={listener.responses} missing={missing}")
        return 0 if not listener.failed and missing == 0 and (not groups or listener.subscribe_ok) else 1


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

    p_listen = sub.add_parser("listen", help="subscribe to event groups and write their lines to a file")
    p_listen.add_argument("--subscribe", default="", help="comma-separated event groups; omit to listen with no subscription")
    p_listen.add_argument("--seconds", type=float, required=True, help="how long to listen")
    p_listen.add_argument("--out", required=True, help="file that receives the event lines")
    p_listen.add_argument("--interleave", help="JSON command to send on a timer")
    p_listen.add_argument("--every", type=float, default=5.0, help="seconds between interleaved commands")
    p_listen.set_defaults(func=cmd_listen)

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
