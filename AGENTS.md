# AGENTS.md

### Shell, scripts, and pyddock
- pyddock tools are strongly preferred because they are trusted; regular shell tools will delay on user approval
- Always prefer pyddock tools before using shell, bash, or powershell; only use shell after actively confirming pyddock tools are not allowed
- Always prefer pyddock tools for file read operations outside of the workspace
- Use run_shell with permitted commands
- Use run_python for free-form shell-like exploration, including scraping and parsing
- Use run_python to compose non-trivial, context-efficient parsing from permitted actions
- Use fs_find and fs_grep for simple file searches


### Optional steering

Always load optional steering when appropriate. Detailed guidance lives in `.agents/steering/`:

- `.agents/steering/build-and-run.md` — How to build, run, check status, and stop the OpenRA game; where logs, crash dumps, and other runtime artifacts live.
- `.agents/steering/agent_text_adventure.md` — How to get into an OpenRA game through the text_adventure tool, and the timeouts and file locations the banner does not give.
- `.agents/steering/debug.md` — Debugging a running game from Python with the `openra_debug` library: breakpoints, stacks, live variable inspection, crash-dump analysis, and safety rules.
