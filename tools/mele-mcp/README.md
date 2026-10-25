# mele-mcp

MCP server that gives Claude Code headless access to Mass Effect Legendary Edition packages through
[LegendaryExplorerCore](https://github.com/ME3Tweaks/LegendaryExplorer) (LEC). Plan and milestones: [docs/05](../../docs/05-toolkit-roadmap.md).

```
Claude Code (WSL) ─stdio─► D:\MELE\dotnet\dotnet.exe D:\MELE\tools\mele-mcp\MeleMcp.dll (Windows) ─► LEC ─► .pcc files
```

Private for now. LEC is GPLv3, so a published version must be GPLv3 too.

## Tools

| Tool | What it does |
|---|---|
| `le_status` | LEC version, resolved LE1/LE2/LE3 game paths, Oodle loaded, init time |
| `le_refresh_files` | Forget the cached game/DLC file list (call after installing or copying files) |
| `pkg_info` | Package summary: names/imports/exports, embedded LE1 talk files, top export classes. Takes a file name (highest mounted copy) or a Windows or `/mnt/<drive>/` path |
| `pkg_search` | Exports filtered by exact class and/or path substring → UIndex, class, instanced full path |
| `pkg_find_name` | Loaded packages whose name table contains a substring (tags, object and Kismet names), optional file wildcard. Partial load: no export data read |
| `export_get` | One export's header (NetIndex, flags, archetype, size) and properties as an indented tree; object references resolved to class, path, UIndex |
| `export_refs` | Exports in the same package that reference an export, and via which property |
| `export_set` | Set one leaf property by path (`VectorParameterValues[11].ParameterValue.G`), save |
| `export_set_batch` | Several leaf properties, any exports of one package, validated together, one save |
| `export_clone` | Duplicate an export and its children (references inside the copy relinked); a level actor is added to the level's `Actors` list |
| `export_import` | Copy an export from another package with everything it references (LEX "clone all references"); globally loaded classes and assets become imports. Optional add to `Actors`. Refuses if the target already has that path |

**Live link** (`game_*`, through ME3Tweaks' LEX Interop ASI, same protocol as LEX):

| Tool | What it does | Tested on LE1 |
|---|---|---|
| `game_status` | Game running, interop pipe present, reply round trip | ✅ |
| `game_actors` | Every visible level with its actors (name, tag), filters | ✅ 21 levels, 1,051 actors |
| `game_actor_get` / `game_actor_set` | Live location, rotation, scale, hidden; move/turn/hide one actor (not saved) | ✅ |
| `game_player_location` | Shepard's position and rotation (reported only after a change, so it may ask you to take a step) | ✅ |
| `game_console`, `game_stream_level`, `game_remote_event` | Fire-and-forget commands | sent, effect not checked |
| `game_hot_reload` | Swap a streamed level for a package file under the name `<level>_LIVE<hhmmss>` | ❌ not proven, see below |

Requirements: the **LEX Interop ASI** (not listed in M3's ASI Manager; install it from LEX → Live Level Editor → requirements → "Interop ASI", which asks M3 to install ASI group 42), and **LEX closed**. The game sends replies to the first window titled "Legendary Explorer", and the server owns a hidden one.

Hot-reload notes: the ASI serves precached bytes only for package names **not found on disk** (`FindPackageFile` hook), so reloading under the same name silently loads the disk file. The rename version is written but untested. **Never fire LE1CP framework events by hand** (`Poll_NPC_*`, `Live_NPC_*`): it re-spawned several NPC files twice and hung LE1 in a loading loop. Streaming a framework-managed `BIONPC_*` file out and in also leaves the framework's NPC dormant (Chakwas vanished).

**Write rule:** the write tools (`export_set*`, `export_clone`, `export_import`) back the package up to `D:\MELE\backups\mele-mcp` (`MELE_BACKUP_DIR`) and refuse any file inside the game install. A file name can resolve to the base game or to another mod's installed copy (LE1CP overrides many level files). Edit the mod's source in the M3 library, then install or copy it into the game.

## Layout

| Path | Content |
|---|---|
| `src/MeleMcp.Core/` | Facade over LEC (`LecHost` init, `GamePaths`, `PackageInspector`, `ExportInspector`, `NameFinder`). Writes go through `PackageWriter.Edit` (guard, mutate, backup, save). No MCP dependency. |
| `src/MeleMcp/` | stdio MCP host (`Program.cs`) and tool classes (`Tools/`) |
| `external/LegendaryExplorer/` | LEX submodule, pinned at tag `6.5.0.6522` (commit `43bf73a`) |
| `scripts/deploy.sh` | Stop running server → publish to `D:\MELE\tools\mele-mcp` → smoke test |
| `scripts/mcp_smoke.py` | JSON-RPC client: initialize, tools/list, calls each tool |
| `scripts/mcp_call.py` | Call one tool from the shell with a fresh server: `mcp_call.py export_get '{"package":"...","export":"35"}'` |

## Setup (already done on this machine, 2026-10-25)

1. .NET SDKs 8.0.426 and 10.0.402 in `D:\MELE\dotnet` (no admin, installed with Microsoft's signed `dotnet-install.ps1 -InstallDir D:\MELE\dotnet`).
2. `git submodule update --init` (shallow).
3. `./scripts/deploy.sh`
4. Registered at project scope in `/.mcp.json` (`claude mcp add mele -s project ...`). Approve it when Claude Code starts in this repo.

## Dev loop

1. Edit code.
2. `./scripts/deploy.sh` (about 5 s; optional argument: a package for the `pkg_info` smoke call).
3. In Claude Code: `/mcp` → `mele` → Reconnect (the deploy stopped the old process).

## Known LEC 6.5 traps for upcoming tools

From [docs/06 §B.1](../../docs/06-le1-first-mod.md#b1-how-lec-65-resolves-le1-dlc-mod-files):

- `AutoloadIni` round-trips lose `PlotManagerConsequenceMap<n>`, `DotU<n>` and `GlobalPackages<n>`. Write our own `AutoLoad.ini` reader/writer before `dlc_new`.
- LEC treats `DLC_MOD*` as a mod folder, but the game's ASIs need `DLC_MOD_` (case-sensitive). Match the game.
- File lookups are cached (`GamePaths.ResolvePackage`). Any tool that installs or copies files must call `MELoadedFiles.InvalidateCaches()`.

## Gotchas found in the M0 spike

| Symptom | Cause | Fix in place |
|---|---|---|
| `CS0121` ambiguous `CopyTo` in `EntryImporter.cs` with SDK 8 | LEC 6.5 targets `net8.0` but needs the C# 14 compiler (first-class spans) that upstream CI gets from a newer SDK | `global.json` pins SDK 10.0.402; output still targets `net8.0` |
| LEC built without registry lookup / native DLLs | LEC only defines `WINDOWS` in its `Win*` configurations | `ProjectReference ... SetConfiguration="Configuration=WinRelease"` |
| Server hangs at startup, no output at all | Default host builder watches `appsettings.json` in the working directory; Claude Code starts us in a `\\wsl.localhost\` directory, where that watcher hangs | `Host.CreateEmptyApplicationBuilder` with `ContentRootPath = AppContext.BaseDirectory` |
| `MELE_LE_DIR` ignored | WSL only passes env vars named in `WSLENV` to Windows processes | `.mcp.json` sets `WSLENV=MELE_LE_DIR` |
| `Permission denied` running `MeleMcp.exe` from WSL | Windows build output on the WSL filesystem has no exec bit | Run via `dotnet.exe MeleMcp.dll` |
| Publish fails: file locked by `.NET Host` | Running server (or a hung test) holds `MeleMcp.dll` | `scripts/stop-server.ps1` runs first in `deploy.sh` |
| `dotnet-install.ps1` IOException when adding an SDK | MSBuild/compiler server processes keep files open in `D:\MELE\dotnet` | `dotnet.exe build-server shutdown` before installing |
| Generic "An error occurred invoking" errors | SDK hides non-`McpException` messages | `ToolErrors.Run` forwards type + message (local server, nothing to hide) |
