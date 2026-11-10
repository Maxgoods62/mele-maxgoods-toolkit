# mele-maxgoods-toolkit

This is my personal toolkit for modding Mass Effect Legendary Edition: LE1, LE2 and LE3. I build what my own mods
need, and I change or remove things when I like. I share it as-is under GPL-3.0. You may use it, fork it or copy parts
of it. There is no support and no roadmap, so issues may go unanswered and pull requests may stay unmerged.

I rely heavily on LLMs to do the work. All of the code here was written by Claude Opus 5.5, through Claude
Code, from my instructions. I decide what to build and I test the results in the game.

## What is in it

| Part | What it is |
|---|---|
| [mele-mcp](tools/mele-mcp/README.md) | An MCP server on Legendary Explorer Core (LEC), with 91 tools: read and edit game files, build mods, install them through ME3Tweaks Mod Manager (M3), work on the running game. |
| [MeleBridge](tools/mele-bridge/README.md) | An ASI, one build per game. Through its pipe, the `live_*` tools read, write and call into the running game. In LE1 it adds an in-game level editor. |
| [lectool](tools/lectool/README.md) | A command-line program on LEC with 26 commands, for build scripts. |
| [release-kit](tools/release-kit/README.md) | Shared release files for my mod repos ([how I release](docs/releasing.md)). |

## Games

Packages, Kismet, script, strings, config, merge mods, M3 installs, live objects and game restarts work in LE1, LE2 and
LE3. Only LE1 has conversation, plot and journal writes, YAML mod builds, the prop catalog, the in-game editor and the
scenario runner. Only LE2 and LE3 have save tools and `dlc_new`. [games.md](docs/games.md) has the full table and what
has run in each game.

## What you need

- Windows x64 with Mass Effect Legendary Edition installed.
- The .NET SDK 10.0.402 or a later 10.0.x to build, and the .NET 8 runtime (x64) to run.
- M3, started once so the server can find it.
- An MCP client that starts stdio servers, such as Claude Code.
- For MeleBridge: WSL with `rsync`, Git for Windows, and the Visual Studio 18 (2026) Build Tools with C++ and CMake.

## Quick start

There are no releases or tags: build from a commit. Clone with the LEC submodule, then build the server in
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

Copy `.mcp.json.example` to `.mcp.json` where you start Claude Code, put your Windows user name in place of `<you>`,
and ask it to call `le_status`. [Getting started](docs/getting-started.md) covers the rest.

## Docs

| Page | For |
|---|---|
| [Formats](docs/formats.md) | pipe protocol, saves, editor files |
| [YAML reference](docs/yaml-reference.md), [example](examples/README.md) | YAML mods (LE1) |
| [Editor guide](docs/editor-guide.md), [design](docs/design/in-game-editor.md) | in-game editor (LE1) |
| [Scenarios](docs/scenarios.md) | in-game test runner (LE1) |
| [LE1 lighting](docs/le1-lighting.md) | why an added NPC can render dark |
| [Translating my mods](docs/translating.md) | translators: how to build a translation mod |
| [Changelog](CHANGELOG.md) | changes by version |

## Game files

The repo holds no game files. Most write tools refuse paths inside the game's folder and back up what they change.
Mods go in through M3 (`mod_install`) and an ASI through its `build.sh --install`, game closed. lectool and a few other
writers make no backup: [getting started](docs/getting-started.md) lists them.

## Related repos

My mods built with it: [mele-pause-anytime](https://github.com/Maxgoods62/mele-pause-anytime),
[mele-photo-mode-revamp](https://github.com/Maxgoods62/mele-photo-mode-revamp) and
[mele-galaxy-map-normandy](https://github.com/Maxgoods62/mele-galaxy-map-normandy).

## Thanks

Built on other people's work, most of it given away for free:

- The [Legendary Explorer](https://github.com/ME3Tweaks/LegendaryExplorer) team at ME3Tweaks and
  [all its contributors](https://github.com/ME3Tweaks/LegendaryExplorer/graphs/contributors): mele-mcp and lectool run
  on Legendary Explorer Core, and years of their work on the game's formats are what every tool here reads and writes.
- ME3Tweaks, for [ME3Tweaks Mod Manager](https://github.com/ME3Tweaks/ME3TweaksModManager), its Starter Kit and its ASI
  loader, and for [LExASIs](https://github.com/ME3Tweaks/LExASIs), the tree MeleBridge builds in.
- [LExSDKv2](https://github.com/llamathings/LExSDKv2), the game's classes in C++, which MeleBridge is compiled with.
- The Mass Effect modding community: the ME3Tweaks Discord, the authors of the Legendary Explorer wiki tutorials, and
  everyone who shares what they find and build.
- BioWare (RIP...), for the games.

## License

GPL-3.0, in `LICENSE`. The open-source libraries the tools build with, and their licenses, are in
[THIRD_PARTY.md](THIRD_PARTY.md) and [MeleBridge's list](tools/mele-bridge/THIRD_PARTY.md).
