# AGENTS.md

A collection of independent personal .NET CLI tools, each a Native AOT console app.
Each tool's notes are in `<Tool>/AGENTS.md`.

| Tool | Binary | Purpose |
|---|---|---|
| [`GitWorktreeTool`](GitWorktreeTool/AGENTS.md) | `gitworktree` | Manage git worktrees |

## Commands

```bash
dotnet build
dotnet run --project test/<Tool>.Test                                  # not `dotnet test` (fails: TUnit on .NET 10 SDK)
dotnet run --project test/<Tool>.Test -- --treenode-filter "/*/*/<Class>/*"
```

Tests need Docker or Podman (detected automatically).

## Conventions

- One folder per tool, tests in `test/<Tool>.Test`, both registered in `Tools.slnx`. Tools never reference each other.
- Package versions only in `Directory.Packages.props` (root: tools, `test/`: tests).
- Warnings are errors, including NuGet vulnerability warnings: upgrade the package, don't suppress the warning.
- AOT: no reflection or reflection-based serialization; check that new dependencies are AOT-compatible.
- CLI: System.CommandLine, one `Build<Name>Command()` per command, logic in a separate method, return exit code `0`/`1`.
- Output: Spectre.Console, errors to stderr (tests check this), diagnostics only with `--debug`, `Markup.Escape` dynamic text.
- Every external program (e.g. `git`) goes through one async wrapper class per tool.
- Descriptive names, small single-purpose methods.
- Every new behaviour or bug fix needs an end-to-end test against the published binary.

## Adding a tool

Copy the project and test setup from `GitWorktreeTool`, register both in `Tools.slnx`, add `<Tool>/AGENTS.md` and a row to the table above.
