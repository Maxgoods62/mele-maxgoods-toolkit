# Examples

This folder holds a small mod that mele-mcp's `mod_build` builds from YAML. It targets LE1, because the YAML builds are LE1
only. Every tool that takes `game` requires it, so each call on this page passes `game` set to `LE1`. The file format
is in the [YAML reference](../docs/yaml-reference.md), and [games.md](../docs/games.md) records how far the example was
tested.

## hello-npc

The mod is called "My Mod", and its DLC folder is `DLC_MOD_MyMod`. It builds one level file, `BIOA_MYM01.pcc`. The level
shows the freighter of LE1's uncharted-world assignments (`BIOA_FRE31_00_LAY`) without that map's scripts or enemies. It has
a start point, `start_mym01`, and a guard tagged `mymod_npc_01`. When Shepard uses the guard, his conversation starts.

| File | What it builds |
|---|---|
| `hello-npc/level.yaml` | The level: the freighter, the start point, the guard imported from a template pawn, and the Kismet (engine script) that starts his conversation |
| `hello-npc/MyModGuard_dlg.yaml` | `MyModGuard_dlg`, in the same level file, copied from the Normandy's requisitions officer. Five of its lines get new text, and the YAML adds two |

The text goes into the conversation's own talk files, so the example needs no GlobalTlk. The lines have no voice, and each
one gets silent lip sync.

### What you need

- mele-mcp 0.1.0 or later, set up as in [getting started](../docs/getting-started.md). Both files hold
  `requires: mele-mcp >= 0.1.0`.
- LE1 with the LE1 Community Patch (LE1CP). The guard's template, `BIONPC_Pressly.pcc`, is a file of LE1CP.

### Build into a test folder

With `out`, the build writes nothing outside that folder except its backups. Both paths are Windows paths or
`/mnt/<drive>/` paths. For a checkout on the WSL file system, `wslpath -w` prints the path to use.

In an MCP client, call `mod_build` with `game` set to `LE1`, `folder` set to this example's full path and `out` set to a
test folder. From a WSL shell, `mcp_call.py` starts a server of its own:

```bash
cd <toolkit checkout>
args=$(python3 -I -c 'import json, sys; print(json.dumps({"game": "LE1", "folder": sys.argv[1], "out": sys.argv[2]}))' \
    "$(wslpath -w examples/hello-npc)" 'C:\mymod-build')
python3 -I tools/mele-mcp/scripts/mcp_call.py mod_build "$args"
```

The level lands in `C:\mymod-build\DLC_MOD_MyMod\CookedPCConsole\BIOA_MYM01.pcc`. The first build runs in this order:

1. It builds `level.yaml`. The level has no conversation yet, so the result lists it in `missingConversations`.
2. It builds `MyModGuard_dlg.yaml`, which copies the template into the level and writes the text.
3. It builds `level.yaml` again (`levelsAgain` in the result) and adds the Kismet that starts the conversation.

A second build changes nothing, and `levelsAgain` stays empty. Delete the folder when you are done.

### Install and try it

1. In M3 (ME3Tweaks Mod Manager), generate a DLC Mod Starter Kit for LE1. Use the mod name `My Mod`, the DLC folder suffix
   `MyMod`, the TLK ID `61230000`, and a mount priority no installed mod uses. M3 writes the mod into its library, with its
   `AutoLoad.ini` and GlobalTlk files.
2. Optional: give the guard his own name, or he keeps the template's. Add these two lines to `level.yaml`:

   ```yaml
   globalTlk: [DLC_MOD_MyMod_GlobalTlk.pcc]   # after 'package'
   ```

   ```yaml
       name: { type: MyModGuard, strref: 61230001, en: "Cargo Guard" }   # in npcs.guard
   ```

3. Call `mod_build` with `game` and `folder`, without `out`. `package` now resolves to the library mod that has
   `DLC_MOD_MyMod`.
4. Call `mod_check` with `game` set to `LE1`, `mod` set to `My Mod` and `languages` set to `INT`.
5. Close the game, then call `mod_install` with `game` set to `LE1` and `mod` set to `My Mod`.
6. In game, open the console and enter `at BIOA_MYM01 start_mym01`.

### Make it yours

- Rename what the mod owns: the DLC folder, the level file, the conversation, the actor type in `name.type`, and the tags.
- Take your own ids. `id_alloc` finds free ones, or checks those you picked.
- Stand where the guard should be and call `npc_place` with `game` set to `LE1` and `file` set to your `level.yaml`. It
  writes `location` and `yaw` and builds the level.
