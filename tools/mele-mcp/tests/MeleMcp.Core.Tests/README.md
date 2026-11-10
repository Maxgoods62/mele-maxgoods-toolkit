# MeleMcp.Core.Tests

Run from WSL, in `tools/mele-mcp`:

```bash
scripts/test.sh                      # everything, about 30 s
scripts/test.sh --filter WriteGuard  # one class
scripts/test.sh --allow-skips        # on a machine without the game
```

`test.sh` builds lectool first and points the lectool tests at it through `LECTOOL`. Plain `dotnet test` works too,
but then the lectool tests are skipped. No CI runs these, so run them before a push.

A test that needs the game, lectool or an admin share is skipped without it, and `dotnet test` still exits 0. So
`test.sh` counts the tests that did not run, prints the reasons, and fails unless you pass `--allow-skips`. Its
output keeps the toolkit's own build warnings and the NuGet audit warnings, and leaves out LegendaryExplorerCore's
build warnings.

## What gets a test

A test is worth having when the code has logic of its own, and a bug there would be silent or would damage a file:
a save, a package, a mod. If a bug would show up right away in a mod build or the first game run, it doesn't need a
test. Code that only passes a call on to LEC or to the running game gets no test, because LEC has its own tests and the
game can't run here.

| File | What it checks |
|---|---|
| `WriteGuardTests.cs` | The game-install guard that every write tool and lectool go through: every spelling of a path inside the install is refused (WSL paths, `\\?\`, junctions, `\\?\Volume{...}`, 8.3 names, `\\localhost\C$`), and folders next to it are not. A fake install in `%TEMP%` stands in for the game. |
| `LectoolTests.cs` | lectool as a build script runs it: an unknown command or flag exits 1, a write into the install or onto an input is refused and leaves the file alone, and a save that fails exits non-zero and leaves the file. That last test runs lectool with the real game folder, on a level under `%TEMP%`. `tlkpackage` writes its package and leaves no temporary file (also with the real game folder). |
| `FixRegressionTests.cs` | One test per bug found by the 2026-11-06 review (`mod_check` ids, `conv_build` lip sync, merge mod arrays). |
| `GameCycleStateTests.cs` | `game_cycle` state found by the 2026-11-07 sweep: `DebugLog.LinesAfter` leaves out a line the game is still writing and skips the rest of one that began before the offset; `GameLock` (the named mutex of `game_cycle`, `game_close`, `mod_install` and LEX Interop sequences) refuses a second taker, takes over a lock whose holder ended, and lets its own thread take it again; `LoadProblem` reads MeleBridge's status as the end of a map load. A temp log file; no game. |
| `KismetStateTests.cs` | The Kismet `level_build` links for a squad, an NPC's conversation and her idle follows the YAML both ways. A `streaming` toggled under a squad, a renamed conversation, and a dropped conversation, idle or squad leave exactly one chain firing, or none. On an empty LE1 level in memory; class defaults and prefabs are read from the game. |
| `LevelBuildStateTests.cs` | `level_build`'s order of writes and what one build leaves for the next. A failed check or refusal creates no level and writes no fork. A refused GlobalTlk leaves the `.m3cd` and `levels.json` describing the saved level and names every file written. A pair's orphan entry or a file that left `streaming.levels` loses its fork. A changed `volume` replaces only the earlier build's copy. A light moved from `lights` to an NPC takes its template's channels back. A usable whose `goTo` left the YAML loads no map any more, and the other usable keeps its chain. A YAML without `sideloader` leaves every `.m3cd` as it is, and the build notes the `ConfigDelta-<level>.m3cd` an earlier build left. |
| `ImportStateTests.cs` | `editor_import` and `npc_place`, from the 2026-11-07 state sweep (M8). An NPC light whose item is gone gets the reason its session entry's state gives: no base, unchanged (dropped), or changed. A build that fails after the YAML is written still returns the import's receipt, `buildError` and the YAML's backup. A lights entry with a misspelt kind fails instead of being read as props. A vanilla actor twice in a session says what became of the first. Only the duplicate test needs the game: it lists the install's folders, read only. |
| `CacheStateTests.cs` | State kept between calls about files on disk: the folder signature that makes LEC's cached file list rescan after an install the tools never ran (fake trees in `%TEMP%`), the rescan rules of `GamePaths.LoadedFiles` (LE1 install, read-only), and a merge mod's `moddesc.ini` listing, taken out when a build does not compile its new manifest and restored byte for byte by list then unlist. |
| `M3StateTests.cs` | ME3Tweaks Mod Manager as the tools drive it, without running it. M3's log is read in whole lines: a line M3 is still writing, or a character cut in two, waits for the next read, and no appended byte is skipped. A merge compile's failure line counts only after the line that begins that manifest's compile. `mergemod_build` holds the M3 lock from before its first write to the end of the compile, and a busy M3 refuses the build with nothing written. The two build tests read class infos from the game, read-only. |
| `YamlBuildStateTests.cs` | The YAML builds' and `id_alloc`'s states from the 2026-11-07 sweep: an out= DLC copy that stops half way is redone, and a later build notes the source files its snapshot lacks; `plot_build` and `mod_build` name every file saved before a failure, with its backup; 50 backups of one file in a burst each get their own name; `id_alloc` searches up to int.MaxValue, reserves the ids it returns per owner, and ends a reservation once an installed source has the id or after 30 days. |
| `PipeStateTests.cs` | The pipe client's side of a request (`LiveBridge.Exchange`) against a stand-in for MeleBridge's end of the pipe. Each request gets an `id` of its own, and a reply with another id is skipped, while one without an id is taken. A `running: true` reply, or no reply, is reported as maybe done (an IOException saying "did not answer", which scenario.py takes for a reply lost to a load). A batch that stops part-way names the requests answered before it and those never sent. The client waits past the bridge's own 1.5 s answer. |
| `AtomicFileTests.cs` | `AtomicFile`, the one way the server writes a whole file: the same bytes as `File.WriteAllText`, with and without a byte order mark, and a write that fails, or that its check refuses before the move, leaves the old file and no temporary file. |
| `PackageSaveTests.cs` | How a package reaches the disk. `PackageWriter.Save` and `Create` write the bytes LEC's own save writes, beside the file and then moved over it, and leave no temporary file; `level_new` writes a level named after its file. A save that fails (a file another program holds open, a folder that is not there) throws, names the file and its backup, and leaves the file's bytes. A save over a hard link replaces the link and leaves the other name's file. Needs the game, for LEC's compression DLL; writes under `%TEMP%` only. |
| `BuildRecordStateTests.cs` | What a build, or a `game_cycle`, says it did when it stops part way (review of the 2026-11-07 sweep's fixes). A fork written over an earlier build's is named as rewritten, not created. `mod_build` names a level file's writes by their own paths (a level it created, `levels.json`). An out= copy's note does not count files a build deleted from the copy. A console command `game_cycle` could not send says "did not run" only when MeleBridge's game thread never took it. The two level tests read class infos and vanilla files from the game, read-only; the others need no game. |

## Rules for new tests

- Never commit a game file. A test that needs one opens it read-only from the install (`TestSetup.RequireGame()`), and
  is skipped when there is no game, which `test.sh` reports.
- Never write into the install. Write under `TestSetup.Temp`. `MELE_BACKUP_DIR` and `MELE_LEVELS_JSON` point there too,
  so backups and `levels.json` stay out of your real folders.
- Before you keep a new test, check that it fails on the bug it is for.
