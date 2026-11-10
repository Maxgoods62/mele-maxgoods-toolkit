# Getting started

This page takes you from a fresh clone to a mele-mcp server that answers your MCP client, then to MeleBridge in a
running game. The steps are the same for LE1, LE2 and LE3, and [games.md](games.md) says what each game supports.

Placeholders: `<toolkit>` is your clone, `<you>` your Windows user, `<LE folder>` the Legendary Edition folder.

## What you need

| What | Needed for | Notes |
|---|---|---|
| Windows x64 | everything | All of it runs on Windows. |
| Mass Effect Legendary Edition | every tool | One folder holds all three: `Game\ME1`, `Game\ME2`, `Game\ME3`. |
| .NET SDK 10.0.402 or a later 10.0.x | building | `global.json` pins it. LEC needs the SDK 10 compiler. |
| .NET 8 runtime, x64 | running mele-mcp | The .NET 10 SDK does not include it. |
| ME3Tweaks Mod Manager (M3) | `mod_install`, `game_cycle`, the ASI loader, its mod library | Start it once, so the server finds it through `HKCU\SOFTWARE\ME3Tweaks`. |
| An MCP client that starts stdio servers | calling the tools | The examples use Claude Code. |
| LE1 Community Patch (LE1CP) | LE1 only: its Sideloader, `level_build`'s default NPC light, the example's pawn | M3 merges the Sideloader config at install. |

An ASI is a DLL that a game loads at start, through an ASI loader. M3 installs the loader, `bink2w64.dll`, in
`Game\ME<n>\Binaries\Win64`. These ASIs are optional. Each one enables tools for the running game.

| ASI | Tools that need it | Install |
|---|---|---|
| MeleBridge | `game_cycle` and `game_close` on LE2 and LE3, `live_*`, the LE1 editor | [Build it](#melebridge) |
| DebugLogger | `game_get`, `game_log`, `game_cycle`, `live_console` output, `game_plot` without MeleBridge | M3's ASI Manager |
| Kismet Logger | `game_kismet_log`, the fired marks of `kismet_read` | M3's ASI Manager |
| LEX Interop | `game_*` tools that send commands, `game_cycle` and `game_close` on LE1, `npc_place`, `game_plot` without MeleBridge | Legendary Explorer (LEX): Live Level Editor, requirements |

`game_status` says whether the LEX Interop pipe answers. Close LEX while you use this ASI: the game sends replies to the
first window titled "Legendary Explorer", which mele-mcp's hidden window is only while one of its calls waits for one.

## Get the source

```bash
git clone https://github.com/Maxgoods62/mele-maxgoods-toolkit
cd mele-maxgoods-toolkit
git submodule update --init
```

The submodule is Legendary Explorer at commit `43bf73a`. mele-mcp and lectool build its LegendaryExplorerCore (LEC) project.

## Build mele-mcp

No built server is published, so you build from source. In PowerShell:

```powershell
cd <toolkit>\tools\mele-mcp
dotnet publish src\MeleMcp\MeleMcp.csproj -c Release -o "$env:LOCALAPPDATA\mele-mcp\server"
& "$env:LOCALAPPDATA\mele-mcp\server\MeleMcp.exe" --selftest
```

`--selftest` prints the `le_status` data, then a summary of `BIOA_CRD00.pcc`. A second argument names another package,
always of LE1, so this check reads LE1 whichever game you mod.

From WSL, `deploy.sh` publishes with the Windows SDK. It needs `dotnet.exe` and `powershell.exe` through interop, and
`python3`.

```bash
cd <toolkit>/tools/mele-mcp
cp build.env.example build.env    # optional: this machine's settings
scripts/deploy.sh                 # or: scripts/deploy.sh <LE1 package>
python3 -I scripts/mcp_call.py pkg_info '{"package":"BioP_Nor.pcc","game":"LE2"}'
```

`deploy.sh` builds first. When the build worked it stops the servers of `MELE_OUT`, because Windows locks a loaded
DLL; a build error stops nothing. Its smoke test uses an LE1 package and fails when the server finds no LE1 folder, or
when a tool that takes `game` does not require it. Reconnect your client afterwards (`/mcp` in Claude Code).
`mcp_call.py` calls one tool on a fresh server.

`./package.sh` at the repo root (WSL) builds a zip in `dist/`. [THIRD_PARTY.md](../THIRD_PARTY.md) says what it holds.

## How mele-mcp finds the game

1. `MELE_LE_DIR`, when set. The server does not check it.
2. The registry: `Install Dir` under `HKLM\SOFTWARE\BioWare\<...> Legendary Edition`, then `InstallLocation` of Steam's
   uninstall entry `Steam App 1328670`. Only a folder with `Game\ME1` counts.

LEC loads the game's Oodle DLL from that folder to read compressed packages.

## Settings

All are optional environment variables. The scripts read the environment first, then an ignored `build.env` in
`tools/mele-mcp` or `tools/mele-bridge`. The server takes Windows or `/mnt/<drive>/` paths.

| Variable | Default | What |
|---|---|---|
| `MELE_LE_DIR` | the registry | The Legendary Edition folder. Also read by lectool and `build.sh --install` and `--uninstall`, as a Windows path. |
| `MELE_BACKUP_DIR` | `%LOCALAPPDATA%\mele-mcp\backups` | Backup root, `<backups>` below. |
| `MELE_LEVELS_JSON` | `%LOCALAPPDATA%\MeleBridge\LE1\levels.json` | The file `level_build` writes. For tests: MeleBridge ignores this setting. |
| `MELE_ID_RESERVATIONS` | `%LOCALAPPDATA%\mele-mcp\id-reservations.json` | The ids `id_alloc` reserved. For tests. |
| `MELE_OWN_DLC` | folders `levels.json` names | Comma-separated DLC folders that `catalog_index` leaves out. |
| `MELE_DUMP_DIR` | `%LOCALAPPDATA%\mele-mcp\dumps` | Dump root. Refused inside the install. |
| `MELE_DOTNET` | `dotnet.exe` on the PATH | Scripts: the Windows `dotnet.exe`, as a WSL path. |
| `MELE_OUT` | `%LOCALAPPDATA%\mele-mcp\server` | Scripts: the publish folder, as a Windows path. In a git worktree `deploy.sh` has no default and asks for it. |
| `MELE_DLL` | `<MELE_OUT>\MeleMcp.dll` | The server that `mcp_call.py`, `mcp_smoke.py` and `scenario.py` run. |
| `MELE_SCENARIO_OUT` | `%LOCALAPPDATA%\mele-mcp\scenarios` | `scenario.py` report root, after `--out` and the first file's `out`. |
| `MELE_VS` | `vswhere`, Visual Studio 18 | Bridge build: the Visual Studio folder, as a WSL path. |
| `MELE_LEXASIS` | `%LOCALAPPDATA%\mele-asi-build\MeleBridge\LExASIs` | Bridge build: the LExASIs checkout, as a WSL path on the Windows disk. |
| `MELE_STAGE` | `dist/` next to `build.sh` | Bridge build: where the `.asi` and `.pdb` go. |
| `LECGAME` | `LE1` | lectool: the game whose object database loads. |
| `EXTRADLLDIR`, `WEMDIR`, `WEMIDS` | none | lectool: `ISACTTools.dll` folder for `ogg`; `.wem` folder and ids for `wwise`. |
| `KIT_ROOT`, `KIT_REF` | toolkit's parent folder, `main` | release-kit: the folder of mod checkouts, and the git ref. |
| `WSLENV` | none | Names each variable passed from WSL to a Windows program. The Python scripts fill it in for `MELE_*`. |
| `LEASI_GAME*_WIN64` | unset | Would make a build copy itself into the game. `build.sh` stops if one is set in Windows. |

## Register the server

For Claude Code on Windows, copy `.mcp.json.example` to `.mcp.json` in the folder you start Claude Code from, and fill
in `<you>`. Settings go into its `env`, with each backslash written twice. To register for all your projects, run this
in a Command Prompt:

```bat
claude mcp add --transport stdio --scope user mele -- "%LOCALAPPDATA%\mele-mcp\server\MeleMcp.exe"
```

For Claude Code in WSL, use the WSL path `/mnt/c/Users/<you>/AppData/Local/mele-mcp/server/MeleMcp.exe`. Publish to a
Windows drive, because on the WSL file system the exe has no exec bit. Name each setting of `env` in `WSLENV` too, such
as `"WSLENV": "MELE_LE_DIR:MELE_OWN_DLC"`.

Other clients start `MeleMcp.exe` with no arguments. It speaks JSON-RPC on stdio and logs to stderr.

## First call: le_status

Ask your client to call `le_status`. It reports all three games.

```json
{
  "lecVersion": "6.5.0.0",
  "legendaryEditionDir": "<LE folder>",
  "legendaryEditionDirFrom": "MELE_LE_DIR",
  "le1GamePath": "<LE folder>\\Game\\ME1",
  "le2GamePath": "<LE folder>\\Game\\ME2",
  "le3GamePath": "<LE folder>\\Game\\ME3",
  "oodleLoaded": true,
  "initMilliseconds": 301
}
```

Then try a read-only call: `pkg_info` with `game` set to `LE1` and `package` to `BIOA_CRD00.pcc`, or with `game` set to
`LE2` or `LE3` and `package` to `BioP_Nor.pcc`. Every tool that takes `game` requires it, so each call names its game:
`LE1`, `LE2` or `LE3`. A tool made for one game needs it too.

| Symptom | Cause | Fix |
|---|---|---|
| `/mcp` shows `mele` as failed | wrong command path, or no .NET 8 runtime | Run `MeleMcp.exe --selftest` and read the error. From WSL: `python3 -I tools/mele-mcp/scripts/mcp_call.py le_status '{}'`. |
| `legendaryEditionDirFrom` is null | no registry entry leads to `Game\ME1` | Set `MELE_LE_DIR`. On WSL, name it in `WSLENV`. |
| Publish fails on a locked file | a running server holds `MeleMcp.dll` | Stop it: `powershell -ExecutionPolicy Bypass -File tools\mele-mcp\scripts\stop-server.ps1 -OutDir "$env:LOCALAPPDATA\mele-mcp\server"`. |

## Where the tools write

Most write tools refuse the Legendary Edition folder. Edit the mod's copy in M3's library, then run `mod_install`.

| What | Where | Guard |
|---|---|---|
| Packages, and what a build writes beside them | the path you name; with `mod_build`'s `out`, under `out` | Install refused. Backup: `<backups>\yyyyMMdd-HHmmss-fff_<file>`. Forks of vanilla levels get none. |
| Level YAML (`editor_import`, `npc_place`, `live_save`) | the file you name | Install refused. Backup in `<backups>` before each write. |
| Screenshots | `game_screenshot`'s `path` (a full path ending in `.png`), or `%LOCALAPPDATA%\mele-mcp\screenshots` | Install refused. No backup: a PNG of that name is replaced. |
| Editor sessions (LE1) | `%LOCALAPPDATA%\MeleBridge\LE1\sessions\<map>.json` | Install refused. `editor_import` adds `<session>.dropped.json`. |
| Prop catalog (LE1) | `%LOCALAPPDATA%\MeleBridge\LE1\catalog`; `mods\LE1\Mele Catalog` in M3's library | Install refused. |
| `.tlk`, `.cnd`, config, `dlc_new`, `pkg_to_text` `outPath` | the path you name | Install refused. |
| Merge mods | `<modRoot>\MergeMods\`, `moddesc.ini`; scratch in `%TEMP%\mele-mcp-merge-<guid>` | The later install patches a core file. Only M3's Restore undoes it. |
| Saves (LE2, LE3) | `Documents\BioWare\Mass Effect Legendary Edition\Save\ME<n>\<career>` | Backup in `<backups>\saves`. Refused while the game runs, unless `allowRunning`. |
| Dumps | `MELE_DUMP_DIR`, a folder per game | They are game code: keep them out of repos. |
| Id reservations (LE1) | `%LOCALAPPDATA%\mele-mcp\id-reservations.json` | Only `id_alloc` reads it. Deleting it drops every reservation. |

These writers make no backup, so check the path you give them:

- With `sideloader`, `level_build` writes `ConfigDelta-<level>.m3cd` over any file of that name, unless an `.m3cd` beside
  the level already names it.
- lectool refuses the Legendary Edition folder as mele-mcp does, and writes where its arguments point with no backup.
  `compileclass` deletes an existing output file.

## MeleBridge

MeleBridge has one ASI per game: `MeleBridge.asi` for LE1, `LE2MeleBridge.asi` and `LE3MeleBridge.asi`. It ships as source
only. The build runs in WSL and needs `rsync`, Git for Windows and network access on the first run. It also needs Visual
Studio 18 (2026) or its Build Tools, with the C++ x64 tools and "C++ CMake tools for Windows". The game PC needs the
latest Visual C++ Redistributable, x64.

```bash
cd <toolkit>/tools/mele-bridge
cp build.env.example build.env     # optional: this machine's settings
./build.sh                         # build and stage LE1's ASI; --game LE2 or LE3 for the others
./build.sh --game LE2 --install    # build, then copy to Game\ME2\Binaries\Win64\ASI, game closed
./build.sh --game LE2 --uninstall  # delete it from there, without building, game closed
```

While that game runs, `--install` builds but does not copy, and `--uninstall` refuses. `--install` does not create the
ASI folder, so install one ASI through M3 first. The log is `Game\ME<n>\Binaries\Win64\Logs\MeleBridge.log`.

To check it, start the game, load a save and call these tools with `game` set:

| Tool | Games | What it tells you |
|---|---|---|
| `live_status` | all | The map, its `BioWorldInfo`, the player. It times out while a map loads. |
| `live_find` with `className` `BioWorldInfo` | all | The object's path. `Pipe MeleBridge_LE<n> not found` means no ASI, or a game still starting. |
| `live_editor` | LE1 | The editor's state. Press F8 in the game to open it. |

A bridge with another pipe protocol is refused, and the error gives both numbers. Build the bridge and the server from
the same checkout.

## Next

[tools/mele-mcp/README.md](../tools/mele-mcp/README.md) lists every tool. [yaml-reference.md](yaml-reference.md) covers
YAML mods (LE1), and [editor-guide.md](editor-guide.md) the in-game editor (LE1).
