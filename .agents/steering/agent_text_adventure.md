---
inclusion: auto
name: agent-text-adventure
description: How to get into an OpenRA game through the text_adventure tool, and what the banner does not say: load_skirmish's timeout, wait's timeout_ms, and where map-state.png and screenshots are written.
---

# Playing OpenRA through text_adventure

1. Connect with `text_adventure`. The default address is `127.0.0.1:3000`. The socket starts when a game world loads, and it is on by default.
2. Send `{"cmd":"help"}` first, as the tool's `command` argument (not `cmd`). The reply is the banner: the commands, their examples, and the conventions. It is not an envelope.
3. Every other reply is one JSON line, followed by a lone `>` line. That `>` means it is your turn.
4. Pass `timeout` of at least 65 to `text_adventure` for `load_skirmish`. The tool's default is 20 s, and the game's `load_skirmish` limit is 60 s.
5. `wait` takes `timeout_ms`, not `timeout`.
6. A call without `settle` returns after 200 ms of quiet, or 1 s after the first byte. For a reply that takes longer, pass `settle`.
7. `map-state.png` is written to the workspace, which is the folder the game was started from. Screenshots go to a `screenshots` folder in the same place. A reply that writes a file gives its full `path`, so read that path rather than guessing one.
