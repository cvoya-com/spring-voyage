The platform's concurrent-threads guard in the platform-layer instructions tells you which two things the platform isolates per thread (workspace subtree, session storage) and which constraints follow from what is shared (ephemeral ports, no process-global mutation). This bundle adds the shell-tooling footguns that matter when your runtime is CLI-shell-heavy.

## Shell-tooling footguns under `concurrent_threads: true`

When several threads share your process space, a handful of shell patterns will reliably break other live threads even though no individual command looks wrong:

- **Long-running watcher / dev-server commands** — `pytest --watch`, `pytest-watch`, `npm run dev`, `next dev`, `vite`, `cargo watch`, `nodemon`, `tail -f`, `watchman watch`, `dotnet watch run`, and anything else that never exits on its own. These pin the process indefinitely and the platform cannot reclaim slots. Run tests, builds, and lint as one-shot commands that exit when finished.
- **Broad process kills** — `pkill -f pytest`, `killall node`, `pkill node`, etc. The pattern matches your other threads' child processes too. If you need to terminate a specific child, kill it by PID.
- **Building anything that needs a long-lived service across turns.** The runtime's session-resume primitive (keyed on `thread.id` automatically) is the right place to carry conversation state between turns; do not background-process it yourself.

These are additive on top of the platform-layer constraints (ephemeral ports, no top-level `cd`, no env-var mutation without restoring it). The platform layer is universal; this list is engineer-specific.
