# MeleMcp.Core.Tests

Run from WSL, in `tools/mele-mcp`:

```bash
scripts/test.sh                      # everything, about 30 s
scripts/test.sh --filter WriteGuard  # one class
```

`test.sh` builds lectool first and points the lectool tests at it through `LECTOOL`. Plain `dotnet test` works too,
but then the lectool tests are skipped. No CI runs these, so run them before a push.

## What gets a test

A test is worth having when the code has logic of its own, and a bug there would be silent or would damage a file:
a save, a package, a mod. If a bug would show up right away in a mod build or the first game run, it doesn't need a
test. Code that only passes a call on to LEC or to the running game gets no test, because LEC has its own tests and the
game can't run here.

| File | What it checks |
|---|---|
| `WriteGuardTests.cs` | The game-install guard that every write tool and lectool go through: every spelling of a path inside the install is refused (WSL paths, `\\?\`, junctions, `\\?\Volume{...}`, 8.3 names, `\\localhost\C$`), and folders next to it are not. A fake install in `%TEMP%` stands in for the game. |
| `LectoolTests.cs` | lectool as a build script runs it: an unknown command or flag exits 1, and a write into the install or onto an input is refused and leaves the file alone. |
| `FixRegressionTests.cs` | One test per bug found by the 2026-11-06 review (`mod_check` ids, `conv_build` lip sync, merge mod arrays). |

## Rules for new tests

- Never commit a game file. A test that needs one opens it read-only from the install (`TestSetup.RequireGame()`), and
  is skipped when there is no game.
- Never write into the install. Write under `TestSetup.Temp`. `MELE_BACKUP_DIR` and `MELE_LEVELS_JSON` point there too,
  so backups and `levels.json` stay out of your real folders.
- Before you keep a new test, check that it fails on the bug it is for.
