# Getting started

These steps take you from a fresh clone to a mele-mcp server that answers in Claude Code, then to MeleBridge in the
running game. Placeholders: `<toolkit checkout>` is your clone of this repo, `<LE folder>` the Mass Effect Legendary
Edition folder (the one that holds `Game\ME1`), `<you>` your Windows user name.

## What you need

| What | Needed for | Notes |
|---|---|---|
| Windows x64 | everything | The server, the game and M3 are Windows programs. M3 needs Windows 10 22H2, Windows 11 24H2 or later. |
| Mass Effect Legendary Edition, with ME1 | every tool | The server finds it in the registry, or through `MELE_LE_DIR` ([how](#how-mele-mcp-finds-the-game)). |
| .NET SDK 10.0.402 or a later 10.0.x | building mele-mcp and lectool | `tools/mele-mcp/global.json` pins it. The projects target `net8.0-windows`, but LEC needs the C# compiler of SDK 10. |
| .NET 8 runtime, x64 | running mele-mcp | The build runs on `Microsoft.NETCore.App` 8.0. The .NET 10 SDK does not include the .NET 8 runtime. |
| ME3Tweaks Mod Manager (M3) 9.2.1 or later | `mod_install`, `game_cycle`, the ASI loader; `mod_build`, `mod_check` and `catalog_build` find mods in its library | Start it once after installing it: mele-mcp finds M3 through the path it records (`ExecutableLocation` under `HKCU\SOFTWARE\ME3Tweaks`). |
| The ASI loader | MeleBridge and the ASIs below | ME3Tweaks' bink proxy (`bink2w64.dll` in `Game\ME1\Binaries\Win64`). M3 installs it. |
| LE1 Community Patch (LE1CP) 2.0 | playing a mod whose levels load through the LE1CP Sideloader | `level_build`'s `sideloader:` key writes the Sideloader config, which M3 merges at install. |
| An MCP client that runs stdio servers | calling the tools | The examples use Claude Code. |

Optional ASIs, for the tools that work on the running game:

| ASI | Tools that need it | Install |
|---|---|---|
| LEX Interop | the `game_*` tools that send commands to the game (`game_log`, `game_kismet_log` and `game_screenshot` only read a file or the window), and `npc_place`, which reads where Shepard stands | In Legendary Explorer, open the Live Level Editor and install the Interop ASI from its requirements; LEX asks M3 to install it. M3's ASI Manager does not list it. |
| DebugLogger | `game_get`, `game_log`, `live_console`, `game_cycle`, and `game_plot` without MeleBridge | M3's ASI Manager |
| Kismet Logger | `game_kismet_log`, and the fired marks of `kismet_read` | M3's ASI Manager (listed for developers) |

Close Legendary Explorer while you use the `game_*` tools: the game sends LEX Interop replies to the first window titled
"Legendary Explorer", and mele-mcp owns a hidden one.

To build MeleBridge:

| What | Why |
|---|---|
| WSL | `build.sh` is a bash script that runs the Windows tools through WSL interop |
| Git for Windows | `build.sh` clones and checks out with `git.exe` |
| Visual Studio 2026 (version 18) Build Tools, with the C++ x64 build tools and "C++ CMake tools for Windows" | the compiler and CMake; `build.sh` finds them with `vswhere`, or takes `MELE_VS` |
| `rsync` in WSL | copies `src/` into the build tree |
| The Microsoft Visual C++ Redistributable, x64, on the game PC | the ASI links the C++ runtime dynamically (`VCRUNTIME140.dll`, `MSVCP140.dll`). It must be at least as new as the MSVC tools that built the ASI, which are 14.50 or later in Visual Studio 2026, so install the latest one. |

Tested with M3 9.2.1 build 137, LE1CP 2.0, LEC from Legendary Explorer 6.5 (the submodule), and these ASIs:
`AutoTOCLE-v2`, `LE1AutoloadEnabler-v13`, `LE1DebugLogger-v8`, `LE1KismetLogger-v4`, `LE1LEXInterop-v10`.

## Get the source

```bash
git clone https://github.com/Maxgoods62/mele-maxgoods-toolkit
cd mele-maxgoods-toolkit
git submodule update --init
```

The submodule is Legendary Explorer at commit `43bf73a` (LEX 6.5). mele-mcp and lectool build its LegendaryExplorerCore
project. Its own nested submodules are not needed.

## Build mele-mcp

No built server is published, so you build it as below. From WSL, `./package.sh` at the repo root can also build the server
into `dist/mele-mcp-v<version>-win-x64.zip`, a server that needs only the .NET 8 runtime.
[THIRD_PARTY.md](../THIRD_PARTY.md#the-release-zip) says what the zip holds.

### On Windows

In PowerShell:

```powershell
cd <toolkit checkout>\tools\mele-mcp
dotnet publish src\MeleMcp\MeleMcp.csproj -c Release -o "$env:LOCALAPPDATA\mele-mcp\server"
```

Run it from `tools\mele-mcp`, where `dotnet` reads `global.json`. Any output folder works. This page uses
`%LOCALAPPDATA%\mele-mcp\server`, which is also the default of `deploy.sh`. The folder then holds `MeleMcp.exe`, which
starts the server, `MeleMcp.dll`, LEC and the libraries they use.

To check the build without an MCP client:

```powershell
& "$env:LOCALAPPDATA\mele-mcp\server\MeleMcp.exe" --selftest
```

It prints the same status as `le_status`, then a summary of `BIOA_CRD00.pcc`. A second argument names another package.

### From WSL

`tools/mele-mcp/scripts/deploy.sh` builds with the Windows .NET SDK, publishes and runs a smoke test. It needs
`dotnet.exe` and `powershell.exe` through WSL interop, and `python3`.

```bash
cd <toolkit checkout>/tools/mele-mcp
cp build.env.example build.env    # optional: this machine's settings
scripts/deploy.sh                 # or: scripts/deploy.sh <package>, for the smoke test's pkg_info call
```

Settings come from the environment, then from `build.env` (not in git). A variable you export wins over the file.

| Variable | Default | What |
|---|---|---|
| `MELE_DOTNET` | `dotnet.exe` on the PATH | The Windows `dotnet.exe`, as a WSL path |
| `MELE_OUT` | `%LOCALAPPDATA%\mele-mcp\server` | The folder to publish to, as a Windows path |
| `MELE_LE_DIR` | unset: the registry | The Legendary Edition folder, as a Windows path; passed on to the smoke test's server only |
| `MELE_DLL` | `<MELE_OUT>\MeleMcp.dll` | The server `mcp_call.py` runs, as a Windows path |

`deploy.sh` does four things in order:

1. It checks that `MELE_DOTNET` has an SDK that `global.json` accepts.
2. It stops the mele-mcp servers that run `MELE_OUT`'s `MeleMcp.dll` or `MeleMcp.exe`, because Windows locks a loaded
   DLL. Servers started from other folders keep running.
3. It runs `dotnet publish src/MeleMcp/MeleMcp.csproj -c Release -nologo -v q -o <MELE_OUT>`.
4. It runs `scripts/mcp_smoke.py`: initialize, the tool list, `le_status`, then `pkg_info` on `BIOA_CRD00_LOC_FR.pcc` or
   the package you gave. The smoke test fails when the server finds no LE1 folder or `pkg_info` fails.

After a deploy, reconnect the server in Claude Code with `/mcp`: the deploy stopped the old process.

`scripts/mcp_call.py` calls one tool on a fresh server, so it works while the client's server is stopped or stale:

```bash
python3 -I scripts/mcp_call.py le_status '{}'
```

It reads the same settings and passes every other `MELE_*` setting on to the server.

## How mele-mcp finds the game

1. `MELE_LE_DIR`, when it is set: the Legendary Edition folder, the one that holds `Game\ME1`, `Game\ME2` and
   `Game\ME3`. A Windows path, or a `/mnt/<drive>/` path, which the server converts.
2. Otherwise the registry, as the game's installer recorded it: `Install Dir` under the
   `HKLM\SOFTWARE\BioWare\<...> Legendary Edition` key, then `InstallLocation` of Steam's uninstall entry
   `Steam App 1328670`. Each is read in the 64-bit registry view first, then the 32-bit one. Only a folder with
   `Game\ME1` counts.

LEC loads the game's Oodle DLL from that folder to read compressed packages. `le_status` says which source gave the
folder.

## Settings

The server reads these environment variables. All are optional. Paths can be Windows paths or `/mnt/<drive>/` paths.

| Variable | Default | What |
|---|---|---|
| `MELE_LE_DIR` | the registry | The Legendary Edition folder ([above](#how-mele-mcp-finds-the-game)) |
| `MELE_BACKUP_DIR` | `%LOCALAPPDATA%\mele-mcp\backups` | Where a write tool copies a package before it saves it |
| `MELE_LEVELS_JSON` | `%LOCALAPPDATA%\MeleBridge\LE1\levels.json` | The `levels.json` that `level_build` writes. `editor_import` reads the editor's sessions from the `sessions` folder beside it. MeleBridge always reads the default file, so set this only to test on copies. |
| `MELE_OWN_DLC` | the DLC folders that `levels.json` says `level_build` built into | Your mods' DLC folders, comma-separated, e.g. `DLC_MOD_MyMod`. `catalog_index` leaves them out, so the prop catalog lists vanilla meshes only. |

## Register the server

### Claude Code on Windows

Copy [`.mcp.json.example`](../.mcp.json.example) to `.mcp.json` in the folder you start Claude Code from, and put your
user name in place of `<you>`, or the path of your own output folder. The toolkit's `.gitignore` keeps `.mcp.json` out
of git.

```json
{
  "mcpServers": {
    "mele": {
      "type": "stdio",
      "command": "C:\\Users\\<you>\\AppData\\Local\\mele-mcp\\server\\MeleMcp.exe",
      "args": [],
      "env": {}
    }
  }
}
```

Claude Code asks you to approve a server from a project's `.mcp.json` the first time it starts there. To register the
server for all your projects instead, run this in a Command Prompt (everything after `--` is the server's command):

```bat
claude mcp add --transport stdio --scope user mele -- "C:\Users\<you>\AppData\Local\mele-mcp\server\MeleMcp.exe"
```

[Settings](#settings) go into `env`. JSON needs each backslash written twice:

```json
"env": {
  "MELE_LE_DIR": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Mass Effect Legendary Edition"
}
```

`MeleMcp.exe` looks for the .NET runtime where the .NET installer puts it. If you installed .NET into a folder of your
own, set `DOTNET_ROOT` to it, or start the DLL with that folder's `dotnet.exe`:

```json
"command": "<dotnet folder>\\dotnet.exe",
"args": ["C:\\Users\\<you>\\AppData\\Local\\mele-mcp\\server\\MeleMcp.dll"]
```

### Claude Code in WSL

Claude Code in WSL starts the Windows server through interop. Give the WSL path of `MeleMcp.exe`:

```json
{
  "mcpServers": {
    "mele": {
      "type": "stdio",
      "command": "/mnt/c/Users/<you>/AppData/Local/mele-mcp/server/MeleMcp.exe",
      "env": {
        "MELE_LE_DIR": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Mass Effect Legendary Edition",
        "WSLENV": "MELE_LE_DIR"
      }
    }
  }
}
```

WSL passes an environment variable to a Windows process only when `WSLENV` names it. List every `MELE_*` setting of
`env` there, separated by colons, e.g. `"WSLENV": "MELE_LE_DIR:MELE_OWN_DLC"`. A setting missing from `WSLENV` never
reaches the server. Leave `MELE_LE_DIR` and `WSLENV` out when the registry lookup finds the game.

Publish to a Windows drive, as `deploy.sh` does by default. On the WSL file system `MeleMcp.exe` has no exec bit, and
starting it fails with `Permission denied`.

### Other MCP clients

Any client that starts stdio servers can run it: the command is `MeleMcp.exe`, with no arguments. The server speaks
JSON-RPC on stdout and logs to stderr.

## First call: le_status

In Claude Code, `/mcp` lists `mele` as connected. Ask Claude to call `le_status`. The first call initializes LEC, then
the answer looks like this:

```json
{
  "lecVersion": "6.5.0.0",
  "legendaryEditionDir": "<LE folder>",
  "legendaryEditionDirFrom": "registry: HKLM\\SOFTWARE\\BioWare\\<...> Legendary Edition 'Install Dir' (64-bit view)",
  "le1GamePath": "<LE folder>\\Game\\ME1",
  "le2GamePath": "<LE folder>\\Game\\ME2",
  "le3GamePath": "<LE folder>\\Game\\ME3",
  "oodleLoaded": true,
  "initMilliseconds": 316
}
```

With `le1GamePath` set and `oodleLoaded` true, the server can open LE1's packages. `legendaryEditionDirFrom` reads
`MELE_LE_DIR` when that variable gave the folder.

A read-only call to try next: `pkg_info` with `package` set to `BIOA_CRD00.pcc` summarizes the highest-mounted copy of
that level file (names, imports, exports, the most common export classes).

### When it fails

| Symptom | Cause | Fix |
|---|---|---|
| `/mcp` shows `mele` as failed | the command path is wrong, or the .NET 8 runtime is missing | Run `MeleMcp.exe --selftest` in a terminal, or from WSL `python3 -I tools/mele-mcp/scripts/mcp_call.py le_status '{}'`, and read the error |
| `legendaryEditionDirFrom` is null, and `le1GamePath` names no folder | `MELE_LE_DIR` is unset and no registry entry leads to a folder with `Game\ME1` | Set `MELE_LE_DIR` |
| `MELE_LE_DIR` has no effect (WSL) | `WSLENV` does not name it | Add it to `WSLENV` |
| `Permission denied` starting `MeleMcp.exe` (WSL) | the output folder is on the WSL file system | Publish to a Windows drive, or start `dotnet.exe` with `MeleMcp.dll` |
| The publish fails on a locked file | a running server holds `MeleMcp.dll` | Stop that client's server, or run `powershell -ExecutionPolicy Bypass -File tools\mele-mcp\scripts\stop-server.ps1 -OutDir "$env:LOCALAPPDATA\mele-mcp\server"`; it stops only the servers of that folder (`-ListOnly` lists them) |
| `CS0121`, an ambiguous `CopyTo` in `EntryImporter.cs` | the build ran with an SDK older than 10 | Build from `tools\mele-mcp` with SDK 10.0.402 or later |
| A write tool answers "Refusing to modify a file inside the game install" | write tools refuse every file in the Legendary Edition folder | Edit the mod's copy in M3's library, then install it with `mod_install` |

## Where the tools write

| What | Where | Setting |
|---|---|---|
| The packages a tool changes or creates | the ones you name, and the files a build writes beside them in the mod (forks, `AutoLoad.ini`, GlobalTlk packages); a file in the Legendary Edition folder is refused | |
| A copy of that package, before each save | `%LOCALAPPDATA%\mele-mcp\backups\<date>-<time>_<file>` | `MELE_BACKUP_DIR` |
| `levels.json`, which MeleBridge reads | `%LOCALAPPDATA%\MeleBridge\LE1\levels.json` | `MELE_LEVELS_JSON` |
| All of a `mod_build` with `out` | under `out`, backups aside; `levels.json` is left alone | the `out` parameter |
| The in-game editor's sessions (MeleBridge) | `%LOCALAPPDATA%\MeleBridge\LE1\sessions\<map>.json` | |
| The prop catalog (`catalog_build`) | `catalog.json` and thumbnails in `%LOCALAPPDATA%\MeleBridge\LE1\catalog`; `MeleCatalog_Core.pcc` in the dev mod `LE1\Mele Catalog` of M3's library | the `outDir` and `modFolder` parameters |
| Screenshots (`game_screenshot`) | `%LOCALAPPDATA%\mele-mcp\screenshots` | the `path` parameter |
| A mod in the game | M3 copies it (`mod_install`, refused while the game runs) | |
| `MeleBridge.asi`, or `LE2MeleBridge.asi`, `LE3MeleBridge.asi` | `<LE folder>\Game\ME<n>\Binaries\Win64\ASI`, copied by `build.sh --install` with the game closed | |
| MeleBridge's log | `<LE folder>\Game\ME<n>\Binaries\Win64\Logs\MeleBridge.log`, rewritten at each game start | |

## MeleBridge

`MeleBridge.asi` runs inside LE1. It gives mele-mcp the `live_*` tools, lets `game_plot` call the game's plot table
directly, and opens the in-game editor on F8. It is distributed as source only, so you build it yourself.

### Build

The tools are listed [above](#what-you-need). The first build downloads ME3Tweaks/LExASIs at commit `aaad03a` with its
submodules, then Dear ImGui v1.93.0 and ImGuizmo at pinned commits.

```bash
cd <toolkit checkout>/tools/mele-bridge
cp build.env.example build.env    # optional: this machine's settings
./build.sh
```

`build.sh` copies `src/` into the LExASIs checkout as one more ASI project, builds it for LE1 (`--game LE2|LE3` for the
others), and copies `MeleBridge.asi` and `MeleBridge.pdb` to `MELE_STAGE`, or `LE2MeleBridge.*`, `LE3MeleBridge.*`.
LExASIs provides the SDK (LExSDKv2), the ASI loader interface and the CMake helpers.

Settings come from the environment, then from `build.env` (not in git). A variable you set wins over the file.

| Variable | Default | What |
|---|---|---|
| `MELE_LE_DIR` | the registry | The Legendary Edition folder, as a Windows path. Only `--install` and `--uninstall` use it. |
| `MELE_VS` | found with `vswhere`: Visual Studio 18 with the C++ x64 tools | The Visual Studio or Build Tools folder, as a WSL path |
| `MELE_LEXASIS` | `%LOCALAPPDATA%\mele-asi-build\MeleBridge\LExASIs` | The LExASIs checkout, as a WSL path. Dear ImGui and ImGuizmo go next to it, in `third_party`. Keep it on the Windows disk: CMake and MSBuild do not work well on `\\wsl.localhost` paths. |
| `MELE_STAGE` | `dist/` next to `build.sh` | Where the `.asi` and `.pdb` are copied, as a WSL path |

### Install

Close the game first:

```bash
./build.sh --install      # build, then copy MeleBridge.asi into <LE folder>\Game\ME1\Binaries\Win64\ASI
./build.sh --uninstall    # remove MeleBridge.asi from that folder
```

Add `--game LE2` or `--game LE3` for those games: `LE2MeleBridge.asi` goes to `Game\ME2`, `LE3MeleBridge.asi` to
`Game\ME3`. Once installed, each runs at every start of that game.

While that game's exe runs, `--install` builds but does not copy, and `--uninstall` refuses. A `tasklist` check that
fails counts as running. You can also copy the staged `MeleBridge.asi` into that folder by hand while the game is
closed. The game needs the ASI loader and the Visual C++ Redistributable ([above](#what-you-need)).

### Check it

Start LE1 and load a save, then call these tools:

- `live_find` with `className` set to `BioGlobalVariableTable` answers through MeleBridge with the path of the plot
  table. Without the bridge it says `Pipe MeleBridge_LE1 not found`, which means the ASI is not installed or the game
  is still starting. A bridge that speaks another pipe protocol is refused, and the error gives both numbers: build and
  install the bridge from the same checkout as the server.
- `game_status` says whether the game runs and whether the LEX Interop pipe answers; its `problem` field names what is
  missing. Only the `game_*` tools and `npc_place` need that pipe.
- `live_editor` reports the in-game editor's state: open or closed, camera, selection.

Then press F8 in the game to open the editor.

## Next

- [yaml-reference.md](yaml-reference.md): the YAML files `mod_build` builds a mod from, and the
  [examples](../examples/)
- [The in-game editor's guide](../tools/mele-bridge/README.md#in-game-editor) in the bridge README: keys, tabs, saving
  a session, `editor_import`
- [The mele-mcp README](../tools/mele-mcp/README.md): every tool, and what each was tested on
- [The editor design](design/in-game-editor.md) and [the shared formats](formats.md)
