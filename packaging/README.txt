mele-mcp @VERSION@ for Windows x64

mele-mcp is an MCP server for modding the Mass Effect Legendary Edition games LE1, LE2 and LE3.
It reads and writes the game's packages with LegendaryExplorerCore and talks to the running
game. Some features are for one game only: YAML mod builds are LE1 only, and save editing is
LE2 and LE3 only. The full list is in docs/games.md, on the repo linked below.

This is a personal tool, shared as-is. It comes with no support and no warranty (see LICENSE).

What it needs:
- Windows x64.
- The .NET 10 runtime, x64: https://dotnet.microsoft.com/download/dotnet/10.0
- Mass Effect Legendary Edition. The server finds it in the registry. If it does not, set
  MELE_LE_DIR to the folder that holds Game\ME1, Game\ME2 and Game\ME3.
- ME3Tweaks Mod Manager, for mod_install and game_cycle.

Unzip everything into a folder of your own, such as %LOCALAPPDATA%\mele-mcp\server, and check
it in a terminal:

    MeleMcp.exe --selftest

It prints what the le_status tool returns, then a summary of BIOA_CRD00.pcc. That file is LE1's,
so this check reads LE1 whichever game you mod.

Most tools take a game parameter, and every one of them requires it: LE1, LE2 or LE3.

To register it with Claude Code for all your projects, run this in a Command Prompt:

    claude mcp add --transport stdio --scope user mele -- "%LOCALAPPDATA%\mele-mcp\server\MeleMcp.exe"

Other MCP clients start MeleMcp.exe with no arguments. It speaks JSON-RPC on stdin and stdout
and logs to stderr.

Settings, the optional ASIs and what to do when it fails:
https://github.com/Maxgoods62/mele-maxgoods-toolkit/blob/main/docs/getting-started.md

mele-mcp is free software under the GNU GPL version 3 (LICENSE). THIRD_PARTY.md lists what it
is built with, and the licenses folder holds their license texts. This build comes from
mele-maxgoods-toolkit @COMMIT@, with Legendary Explorer @LEC_COMMIT@ as its submodule.
Sources:
- mele-mcp: https://github.com/Maxgoods62/mele-maxgoods-toolkit
- LegendaryExplorerCore and TexConverter.dll: https://github.com/ME3Tweaks/LegendaryExplorer
- CompressionWrappers.dll: https://github.com/MassEffectModder/MassEffectModder

The zip holds no Oodle. The server loads oo2core_8_win64.dll from your game.
