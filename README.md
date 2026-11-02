# mele-maxgoods-toolkit

My own tools for modding Mass Effect Legendary Edition, mostly LE1, from an MCP client such as Claude Code. They read
and edit the game's packages, build mods from YAML files, install them through ME3Tweaks Mod Manager and work on the
running game. I build what my mods need and change or remove things when I like. The code is shared as-is under GPL-3.0:
use it, fork it or copy from it. There is no support and no roadmap. Issues may go unanswered and pull requests
unmerged.

| Tool | What it is |
|---|---|
| [mele-mcp](tools/mele-mcp/README.md) | An MCP server (C#, stdio) on [LegendaryExplorerCore](https://github.com/ME3Tweaks/LegendaryExplorer) (LEC). Its 91 tools inspect and edit packages, conversations, plot and journal data and Kismet, build a mod's levels, plot and conversations from YAML, check and install the mod, drive the running game, and read and edit LE2 and LE3 saves. |
| [MeleBridge](tools/mele-bridge/README.md) | `MeleBridge.asi`, an ASI for LE1. It opens a named pipe into the running game, through which mele-mcp's `live_*` tools read and write any property of a live object, call UnrealScript functions and run console commands. F8 opens a level editor drawn over the game. |
| [lectool](tools/lectool/README.md) | A command-line tool on the same LEC for steps mele-mcp has no tool for: compiling script classes into a package, adding a script actor to a level, porting a mesh, building an LE1 sound, building and checking an M3 2DA merge, building an actor type from two packages and checking the result, copying a GFx movie into a package of its own. |
| [release-kit](tools/release-kit/README.md) | One set of release files for the mods: `kit.sh` renders the GitHub workflow, the release notes and repo lock scripts and the release docs into each repo, shows when a repo has drifted, and scans a repo for game files and private strings. |

## Versions

There are no releases or tags: build from a commit. The toolkit's version is mele-mcp's, 0.1.0 (`Version` in
`tools/mele-mcp/src/MeleMcp/MeleMcp.csproj`), and a mod YAML's `requires:` line is checked against it. MeleBridge is ASI
version 2 and speaks pipe protocol 1. mele-mcp refuses a bridge that speaks another protocol, so build both from the
same commit.

The package, export, Kismet, FaceFX, string, script, config and GFx tools, conversation, plot and journal reading, and
`mod_install` also take LE2 and LE3. Conversation, plot and journal writing, the YAML builds and the prop catalog are
LE1 only. MeleBridge also runs on LE2 and LE3, without the editor, and `live_*` passed one test run on each. All other
in-game testing was done on LE1: the rest of LE2 and LE3 support is tested on files, not in the game.

## Requirements

- Windows x64, with Mass Effect Legendary Edition installed
- the .NET SDK 10.0.402 or a later 10.0.x to build mele-mcp, and the .NET 8 runtime (x64) to run it
- ME3Tweaks Mod Manager (M3), which installs mods and the ASI loader
- an MCP client that runs stdio servers (the examples use Claude Code)
- for MeleBridge: WSL with `rsync`, Git for Windows and the Visual Studio 2026 Build Tools with C++ and CMake

Versions, the optional ASIs and the LE1 Community Patch are in [getting started](docs/getting-started.md#what-you-need).

## Quickstart

Clone with the LEC submodule:

```bash
git clone https://github.com/Maxgoods62/mele-maxgoods-toolkit
cd mele-maxgoods-toolkit
git submodule update --init
```

Build mele-mcp in PowerShell. Run it from `tools\mele-mcp`, where `global.json` pins the SDK:

```powershell
cd tools\mele-mcp
dotnet publish src\MeleMcp\MeleMcp.csproj -c Release -o "$env:LOCALAPPDATA\mele-mcp\server"
```

From WSL, `tools/mele-mcp/scripts/deploy.sh` publishes to the same folder and runs a smoke test.

Register the server: copy [`.mcp.json.example`](.mcp.json.example) to `.mcp.json` in the folder you start Claude Code
from, and replace `<you>` in the path with your Windows user name. Start Claude Code, approve the `mele` server, and ask
it to call `le_status`. A working server reports your Legendary Edition folder, the LE1 game path and
`"oodleLoaded": true`. The server finds the game in the registry; set `MELE_LE_DIR` when it does not.

[Getting started](docs/getting-started.md) has the WSL setup, every setting, what to do when `le_status` fails, and the
MeleBridge build.

## Documentation

| Page | What it covers |
|---|---|
| [docs/getting-started.md](docs/getting-started.md) | Requirements, building mele-mcp, settings, MCP registration, the first call, building and checking MeleBridge |
| [docs/yaml-reference.md](docs/yaml-reference.md) | The YAML files `mod_build` reads: levels, NPCs, props, lights, plot, journal, conversations |
| [examples/](examples/) | Example mods |
| [tools/mele-mcp/README.md](tools/mele-mcp/README.md) | Every tool, and what each was tested on |
| [tools/mele-bridge/README.md](tools/mele-bridge/README.md) | The in-game editor's user guide (keys, tabs, session files), the pipe ops |
| [tools/lectool/README.md](tools/lectool/README.md) | lectool's commands |
| [docs/design/in-game-editor.md](docs/design/in-game-editor.md) | How the in-game editor works, why it was built that way, and its limits |
| [docs/formats.md](docs/formats.md) | The contracts mele-mcp and MeleBridge share: pipe protocol, `levels.json`, session files, `catalog.json` |
| [docs/le1-lighting.md](docs/le1-lighting.md) | Why an NPC a mod adds to LE1 can render darker than the vanilla NPC beside her, and the lights the toolkit gives her |
| [CHANGELOG.md](CHANGELOG.md) | Changes per version |
| [THIRD_PARTY.md](THIRD_PARTY.md) | Third-party code in mele-mcp and lectool, and its licenses; MeleBridge has its own list in [tools/mele-bridge/THIRD_PARTY.md](tools/mele-bridge/THIRD_PARTY.md) |

## Repo layout

| Path | What |
|---|---|
| `tools/mele-mcp/src/MeleMcp/` | The stdio MCP host (`Program.cs`) and the tool classes (`Tools/`) |
| `tools/mele-mcp/src/MeleMcp.Core/` | The work behind the tools, on LEC, with no MCP dependency |
| `tools/mele-mcp/external/LegendaryExplorer/` | Legendary Explorer as a git submodule, pinned at commit `43bf73a` (LEX 6.5) |
| `tools/mele-mcp/scripts/` | `deploy.sh` (build, publish, smoke test from WSL), `mcp_call.py` (call one tool from a shell), `mcp_smoke.py`, `stop-server.ps1` |
| `tools/mele-bridge/` | MeleBridge's C++ sources (`src/`) and `build.sh`, which builds it inside a checkout of [ME3Tweaks/LExASIs](https://github.com/ME3Tweaks/LExASIs) |
| `tools/lectool/` | lectool (`Program.cs`, `lectool.csproj`) |
| `docs/` | The pages listed above |
| `examples/` | Example mods |
| `.mcp.json.example` | A Claude Code registration for the server |

## Game files and where the tools write

The repo holds no game files: the tools read them from your own Legendary Edition install, and write where you point
them. A write tool changes the package you name and refuses any file inside the Legendary Edition folder. Before each
save it copies the package to `%LOCALAPPDATA%\mele-mcp\backups` (`MELE_BACKUP_DIR`). `level_build` writes
`levels.json` for MeleBridge to `%LOCALAPPDATA%\MeleBridge\LE1` (`MELE_LEVELS_JSON`), where the in-game editor also
saves its sessions. mele-mcp installs a mod only through M3 (`mod_install`), which it refuses while the game runs. The
one file per game the toolkit copies into the game folder itself is MeleBridge's ASI, and only when you run
`build.sh --install`. The full list is in [getting started](docs/getting-started.md#where-the-tools-write).

## Related repos

- [mele-pause-anytime](https://github.com/Maxgoods62/mele-pause-anytime): Pause Anytime, `LE<n>PauseAnytime.asi`, an ASI
  for LE1, LE2 and LE3. Esc or a gamepad's Start pauses a conversation, a cutscene or some fullscreen movies. On a paused conversation or cutscene, a gamepad's Y or F7 opens photo mode.
- [mele-galaxy-map-normandy](https://github.com/Maxgoods62/mele-galaxy-map-normandy): a script mod that puts the SR-1
  Normandy on LE1's galaxy map. Its `build.sh` uses mele-mcp and lectool from this repo.

## License

GPL-3.0, see [LICENSE](LICENSE). LegendaryExplorerCore, which mele-mcp and lectool build on, is GPL-3.0 as well.

`MeleBridge.asi` is distributed as source only. Build it with `tools/mele-bridge/build.sh`.
