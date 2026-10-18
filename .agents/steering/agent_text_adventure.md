---
inclusion: auto
name: agent-text-adventure
description: How to get into an OpenRA game through the text_adventure tool, and what the banner does not say: load_skirmish's timeout_ms, wait's timeout_ms, and where map-state.png and screenshots are written.
---

# Playing OpenRA through text_adventure

1. Connect with `text_adventure`. The default address is `127.0.0.1:3000`. The socket starts when a game world loads, and it is on by default. A world may already be running: `{"cmd":"state"}` shows what it is, and playing it is fine — starting a new one with `load_skirmish` is your choice.
2. Send `{"cmd":"help"}` first, as the tool's `command` argument (not `cmd`). The reply is the banner: the commands, their examples, and the conventions. It is not an envelope.
3. Every other reply is one JSON line, followed by a lone `>` line. That `>` means it is your turn.
4. Pass the tool's `timeout_ms` of at least 65000 for `load_skirmish`. The tool's default is 20000 ms, and the game's `load_skirmish` limit is 60 s. If the reply outruns the tool's quiet window, pass `settle_ms` too (step 6).
5. The `wait` command's request key is `timeout_ms`; there is no `timeout` key.
6. A call without `settle_ms` returns after 200 ms of quiet, or 1 s after the first byte. For a reply that takes longer, pass `settle_ms`.
7. `map-state.png` is written to the workspace, which is the folder the game was started from. Screenshots go to a `screenshots` folder in the same place. A reply that writes a file gives its full `path`, so read that path rather than guessing one.
