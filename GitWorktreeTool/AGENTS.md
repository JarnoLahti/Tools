# GitWorktreeTool (`gitworktree`)

Creates worktrees under `.worktree/<target>` and copies the gitignored files listed in `.worktreeinclude`
(secrets, local config) into them. Commands: `init`, `include`, `delete` (see `--help`).
All `git` calls go through `GitProcessWrapper`.

## Pattern matching (keep it git-compatible)

- No slash: matches at any depth. Contains a slash: relative to the repo root.
- `*` and `?` are supported; `**` and `!` are not.
- `.gitignore` is read from the source branch through git, not from disk.

## Tests

- End-to-end in a container; the fixture repo is built in `SetupContainer()`.
- Add fixture files before the initial commit there.
- Tests share one repo, so name branches and worktrees with `UniqueName(...)`.
