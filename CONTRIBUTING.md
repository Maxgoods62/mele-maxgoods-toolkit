# Contributing

How to build and test the toolkit, where the design notes are, and the rules a change keeps.

## Sources

```bash
git clone https://github.com/Maxgoods62/mele-toolkit.git
cd mele-toolkit
git submodule update --init    # LegendaryExplorerCore, in tools/mele-mcp/external/LegendaryExplorer
```

mele-mcp and lectool build LegendaryExplorerCore (LEC) from that submodule, so neither builds without it.

You work in WSL on Windows, with LE1 installed. The scripts call Windows programs through interop. mele-mcp and lectool
need the Windows .NET SDK that their `global.json` asks for (10.0.402 or a later 10.0 feature band). MeleBridge needs
Visual Studio 18 (2026) or its Build Tools with C++ ([the bridge README](tools/mele-bridge/README.md#build-and-install)).

## Build and test

Each tool folder with a `build.env.example` reads its settings from a `build.env` beside it, which git ignores. Copy
the example and set what differs on your machine; a variable exported in the environment wins over the file.

### mele-mcp

```bash
cd tools/mele-mcp
scripts/deploy.sh                                   # publish to MELE_OUT, then run the smoke test
python3 -I scripts/mcp_call.py le_status '{}'       # call one tool on a fresh server
```

- `deploy.sh` publishes `src/MeleMcp/MeleMcp.csproj` (Release) to `MELE_OUT`, a Windows path (default
  `%LOCALAPPDATA%\mele-mcp\server`). First it stops the servers that run that folder's `MeleMcp.dll`, since Windows
  locks loaded DLLs; servers started from other folders keep running. Reconnect the server in your MCP client
  afterwards (`/mcp` in Claude Code).
- Then it runs `scripts/mcp_smoke.py`: initialize, list the tools, call `le_status`, and `pkg_info` on
  `BIOA_CRD00_LOC_FR.pcc` (or the package you pass to `deploy.sh`). It fails when the server finds no LE1 folder or
  `pkg_info` fails.
- `scripts/mcp_call.py <tool> ['<json arguments>']` starts a server process of its own for each call, so it works
  while your client's server is stopped or stale. It runs `MELE_OUT`'s `MeleMcp.dll`, or the one `MELE_DLL` names.
- Settings (`build.env.example` explains each): `MELE_DOTNET`, `MELE_LE_DIR`, `MELE_OUT`, `MELE_DLL`. The two Python
  scripts pass every other `MELE_*` variable on to the server, such as `MELE_OWN_DLC`, `MELE_LEVELS_JSON` and
  `MELE_BACKUP_DIR`.
- `./package.sh`, at the repo root, builds the release zip and `SHA256SUMS` in `dist/`; it reads `MELE_DOTNET` like
  `deploy.sh`, and [THIRD_PARTY.md](THIRD_PARTY.md#the-release-zip) says what the zip holds.

### MeleBridge

```bash
cd tools/mele-bridge
./build.sh              # build, and stage MeleBridge.asi in dist/ (MELE_STAGE)
./build.sh --install    # also copy it to the game's ASI folder; the game must be closed
```

The bridge README covers the settings and the in-game editor. With the game running, check the bridge from
`tools/mele-mcp` with `python3 -I scripts/mcp_call.py live_editor '{}'`; that call answers even while the game loads.

### lectool

```bash
cd tools/lectool
"$MELE_DOTNET" build lectool.csproj -c Release -o '<out dir>'
```

See [its README](tools/lectool/README.md) for how to run it.

### Parallel worktrees

A worktree publishes mele-mcp to a `MELE_OUT` of its own (`MELE_OUT='C:\mele-mcp-test' scripts/deploy.sh`), so its
deploy stops and replaces only its own server. A worktree's bridge build stages in its own `dist/`. Two worktrees with the
same `MELE_LEXASIS` share one LExASIs checkout and build one after the other.

## Design notes

- [docs/design/in-game-editor.md](docs/design/in-game-editor.md) says how the editor works and why. Code comments cite
  its section numbers (`§10`, `§12.4`), so a section is never renumbered.
- [docs/formats.md](docs/formats.md) holds the pipe protocol and the files MeleBridge and mele-mcp share. Its §1 says
  when a version goes up: the pipe protocol when an op changes its arguments or its reply (`MELE_PROTOCOL_VERSION` in
  `tools/mele-bridge/src/SharedVersion.h` and `Versions.BridgeProtocol` in mele-mcp move together), and a file's version
  when an older reader would do something wrong with the new content. A key an older reader would only skip needs no
  bump. Update the page in the same change.
- [docs/le1-lighting.md](docs/le1-lighting.md): how LE1 lights characters, and what that means for a new NPC.

## The SDK layout trap

LExSDKv2's C++ classes do not always match the engine's memory layout. Its `FRwVector3` is 12 bytes in C++ and 16 in
the engine, so `FBioRwBox` compiles to 0x28 bytes instead of 0x30. Every later field of `UParticleSystem` is then 8 bytes
off, and so are `UParticleSystemComponent::Template` and `bAutoActivate`. MeleBridge reads those fields at the offsets
the engine's reflection gives (`PropertyOffset` in `src/Reflection.cpp`), and pins the other fields it reads with
`static_assert` (`src/Editor.cpp`). Before you read a field that comes after an `FRwVector3`, or after a struct that holds
one, check its offset against reflection ([design §12.7](docs/design/in-game-editor.md#127-findings)).

## Rules

- No game files or binaries in git: no `.pcc .tfc .bik .isb .afc .wav .ogg .wem .tlk .sav .dll .asi .exe`. This prints
  nothing in a clean history:

  ```bash
  git log --all --name-only --format= | sort -u | grep -iE '\.(pcc|tfc|bik|isb|afc|wav|ogg|wem|tlk|sav|dll|asi|exe)$'
  ```

- No machine paths in tracked files. Your folders go in `build.env`, which stays out of git.
- Nothing writes inside the game install. Mods reach the game through ME3Tweaks Mod Manager (`mod_install`) with the
  game closed. The one exception is `MeleBridge.asi`, which `build.sh --install` copies into the ASI folder, game closed.
- The toolkit is GPL-3.0 ([LICENSE](LICENSE)), and a change is offered under it.
- New dependencies go into [THIRD_PARTY.md](THIRD_PARTY.md) (mele-mcp, lectool) or
  [tools/mele-bridge/THIRD_PARTY.md](tools/mele-bridge/THIRD_PARTY.md), with their license.
