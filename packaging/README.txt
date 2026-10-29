mele-mcp @VERSION@ for Windows x64

mele-mcp is an MCP server for modding Mass Effect Legendary Edition, LE1 first. It reads and
writes the game's packages with LegendaryExplorerCore, builds mods from YAML files and talks to
the running game.

What it needs:
- Windows x64.
- The .NET 8 runtime, x64: https://dotnet.microsoft.com/download/dotnet/8.0
- Mass Effect Legendary Edition. The server finds it in the registry. If it does not, set
  MELE_LE_DIR to the folder that holds Game\ME1.
- ME3Tweaks Mod Manager, for mod_install and game_cycle.

Unzip everything into a folder of your own, such as %LOCALAPPDATA%\mele-mcp\server, and check
it in a terminal:

    MeleMcp.exe --selftest

It prints what the le_status tool returns, then a summary of BIOA_CRD00.pcc.

To register it with Claude Code for all your projects, run this in a Command Prompt:

    claude mcp add --transport stdio --scope user mele -- "%LOCALAPPDATA%\mele-mcp\server\MeleMcp.exe"

Other MCP clients start MeleMcp.exe with no arguments. It speaks JSON-RPC on stdin and stdout
and logs to stderr.

Settings, the optional ASIs and what to do when it fails:
https://github.com/Maxgoods62/mele-toolkit/blob/main/docs/getting-started.md

mele-mcp is free software under the GNU GPL version 3 (LICENSE). THIRD_PARTY.md lists what it
is built with, and the licenses folder holds their license texts. This build comes from
mele-toolkit @COMMIT@, with Legendary Explorer @LEC_COMMIT@ as its submodule.
Sources:
- mele-mcp: https://github.com/Maxgoods62/mele-toolkit
- LegendaryExplorerCore and TexConverter.dll: https://github.com/ME3Tweaks/LegendaryExplorer
- CompressionWrappers.dll: https://github.com/MassEffectModder/MassEffectModder

The zip holds no Oodle. The server loads oo2core_8_win64.dll from your game.
