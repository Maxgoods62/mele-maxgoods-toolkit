# mele-maxgoods-toolkit

This is my personal toolkit for modding Mass Effect Legendary Edition: LE1, LE2 and LE3. I build what my own mods
need, and I change or remove things when I like. I share it as-is under GPL-3.0. You may use it, fork it or copy parts
of it. There is no support and no roadmap, so issues may go unanswered and pull requests may stay unmerged.

## What is in it

| Part | What it is |
|---|---|
| [mele-mcp](tools/mele-mcp/README.md) | An MCP server on Legendary Explorer Core (LEC), with 91 tools. They read and edit game files, build mods, install them through ME3Tweaks Mod Manager (M3) and work on the running game. |
| [MeleBridge](tools/mele-bridge/README.md) | An ASI with one build per game. Through its pipe, the `live_*` tools read and write objects in the running game and call UnrealScript functions. The LE1 build adds an in-game level editor. |
| [lectool](tools/lectool/README.md) | A command-line program on LEC with 26 commands, for build scripts and jobs mele-mcp has no tool for. |
| [release-kit](tools/release-kit/README.md) | Shared release files for my mod repos. [Releasing](docs/releasing.md) says how I release and protect them. |

## Games

| Area | LE1 | LE2 | LE3 |
|---|---|---|---|
| Packages, Kismet, script, strings, config | yes | yes | yes |
| Read conversations, plot and journal | yes | yes | yes |
| Write conversations, plot and journal | yes | no | no |
| YAML mod builds, prop catalog | yes | no | no |
| Start a mod | M3's Starter Kit, then `mod_build` | `dlc_new` | `dlc_new` |
| Merge mods, install through M3 | yes | yes | yes |
| Saves | no | yes | yes |
| Live objects through MeleBridge | yes | yes | yes |
| Restart the game on a save, close it | yes | yes | yes |
| In-game editor, scenario runner | yes | no | no |

[games.md](docs/games.md) has the full table, its limits and what has run in each game. Every tool that takes `game`
requires it: `LE1`, `LE2` or `LE3`.

## What you need

- Windows x64 with Mass Effect Legendary Edition installed.
- The .NET SDK 10.0.402 or a later 10.0.x to build, and the .NET 8 runtime (x64) to run.
- M3, started once so the server can find it.
- An MCP client that starts stdio servers. The examples use Claude Code.
- For MeleBridge: WSL with `rsync`, Git for Windows, and the Visual Studio 18 (2026) Build Tools with C++ and CMake.

## Quick start

There are no releases or tags, so build from a commit. Clone with the LEC submodule, then build the server in
PowerShell:

```bash
git clone https://github.com/Maxgoods62/mele-maxgoods-toolkit
cd mele-maxgoods-toolkit
git submodule update --init
```

```powershell
cd tools\mele-mcp
dotnet publish src\MeleMcp\MeleMcp.csproj -c Release -o "$env:LOCALAPPDATA\mele-mcp\server"
```

Copy `.mcp.json.example` to `.mcp.json` in the folder you start Claude Code from, and replace
`<you>` with your Windows user name. Then ask the client to call `le_status`. It reports the Legendary Edition folder
and the three game paths. [Getting started](docs/getting-started.md) covers WSL, settings, the optional ASIs and
what to do when it fails.

## Docs

| Page | What it covers |
|---|---|
| [Getting started](docs/getting-started.md) | Build, settings, registration, where tools write |
| [Games](docs/games.md) | What works on each game, and what has run there |
| [Formats](docs/formats.md) | Pipe protocol, LE2 and LE3 saves, LE1 editor files |
| [YAML reference](docs/yaml-reference.md) | YAML mod keys (LE1) |
| [Examples](examples/README.md) | A small YAML mod (LE1) |
| [Editor guide](docs/editor-guide.md) | Using the in-game editor (LE1) |
| [Editor design](docs/design/in-game-editor.md) | How the editor works (LE1) |
| [Scenarios](docs/scenarios.md) | In-game test runner (LE1) |
| [LE1 lighting](docs/le1-lighting.md) | Why an added NPC can render dark |
| [Changelog](CHANGELOG.md) | Changes by version |

## Game files

The repo holds no game files. Most write tools refuse paths inside the Legendary Edition folder and back up what they
change. Edit the mod's copy in M3's library, then install it with `mod_install`, game closed. An ASI goes in through
its `build.sh --install`, also with the game closed. lectool and a few other writers have no guard:
[getting started](docs/getting-started.md) lists them.

## Related repos

- [mele-pause-anytime](https://github.com/Maxgoods62/mele-pause-anytime): an ASI that pauses LE1, LE2 and LE3 during
  conversations, cutscenes and some movies.
- [mele-photo-mode-revamp](https://github.com/Maxgoods62/mele-photo-mode-revamp): an ASI that replaces the photo mode
  panel in LE1, LE2 and LE3, with a free camera, lens and colour controls, hi-res shots and presets.
- [mele-galaxy-map-normandy](https://github.com/Maxgoods62/mele-galaxy-map-normandy): an LE1 galaxy map mod built with
  lectool and mele-mcp.

## License

GPL-3.0, in `LICENSE`. Third-party licenses are in [THIRD_PARTY.md](THIRD_PARTY.md) and
[MeleBridge's list](tools/mele-bridge/THIRD_PARTY.md).
