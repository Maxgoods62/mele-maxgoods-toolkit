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
| `pkg_info` | Package summary: names/imports/exports, embedded LE1 talk files, top export classes. Takes a file name (highest mounted copy) or a Windows or `/mnt/<drive>/` path |

## Layout

| Path | Content |
|---|---|
| `src/MeleMcp.Core/` | Facade over LEC (`LecHost` init, `GamePaths`, `PackageInspector`). No MCP dependency. |
| `src/MeleMcp/` | stdio MCP host (`Program.cs`) and tool classes (`Tools/`) |
| `external/LegendaryExplorer/` | LEX submodule, pinned at tag `6.5.0.6522` (commit `43bf73a`) |
| `scripts/deploy.sh` | Stop running server → publish to `D:\MELE\tools\mele-mcp` → smoke test |
| `scripts/mcp_smoke.py` | JSON-RPC client: initialize, tools/list, calls each tool |

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
