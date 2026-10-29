# Examples

Example mods built from YAML files with mele-mcp's `mod_build`. The file format is in the
[YAML reference](../docs/yaml-reference.md).

| Folder | What it builds |
|---|---|
| [hello-npc](hello-npc/) | A map of its own with an NPC who talks |

## hello-npc

The example mod is called "My Mod", and its DLC folder is `DLC_MOD_MyMod`. It builds one level file, `BIOA_MYM01.pcc`.
The level shows the freighter of LE1's uncharted-world assignments (`BIOA_FRE31_00_LAY`) without that map's scripts or
enemies, inside a copy of the map's stream volume. It has a start point, `start_mym01`, and a guard (tag `mymod_npc_01`).
When Shepard uses the guard, his conversation starts.

| File | What it builds |
|---|---|
| [`level.yaml`](hello-npc/level.yaml) | The level: the streamed freighter, its post-process, the start point, the guard imported from a template pawn, and the Kismet that starts his conversation |
| [`MyModGuard_dlg.yaml`](hello-npc/MyModGuard_dlg.yaml) | The conversation `MyModGuard_dlg`, in the same level file. The first build copies it from the Normandy's requisitions officer (`nor20_req_officer_dlg`) under its own names and ids. The YAML gives five of its lines new text and adds two lines |

The text goes into the conversation's own talk files inside the level, so the example needs no GlobalTlk. No line with
new text has a voice: the build gives each one silent lip sync.

The example builds, and a second build changes nothing. It has not been played in game.

### What you need

- mele-mcp 0.1.0 or later, registered in your MCP client ([getting started](../docs/getting-started.md)). Both files
  hold `requires: mele-mcp >= 0.1.0`, and an older server builds nothing.
- LE1 with the LE1 Community Patch (LE1CP) installed. The guard's template, `BIONPC_Pressly.pcc`, is a file of LE1CP.
  The freighter files and the conversation template are base game files. LE1CP has its own copy of the template's file,
  and the build takes that one when it is installed.

### Build into a test folder

`mod_build` takes the example's folder (`folder`) and, with `out`, a folder to build into. With `out` the build writes
nothing outside that folder except its backups, so you can run it without a mod of your own. Both paths are Windows
paths or `/mnt/<drive>/` paths. A checkout on the WSL file system has a `\\wsl.localhost\<distro>\...` path, which
`wslpath -w` prints.

In Claude Code, ask for it:

```text
Call mod_build with folder <toolkit checkout>\examples\hello-npc and out C:\mymod-build.
```

From a WSL shell, `mcp_call.py` starts a server of its own:

```bash
cd <toolkit checkout>
args=$(python3 -I -c 'import json, sys; print(json.dumps({"folder": sys.argv[1], "out": sys.argv[2]}))' \
    "$(wslpath -w examples/hello-npc)" 'C:\mymod-build')
python3 -I tools/mele-mcp/scripts/mcp_call.py mod_build "$args"
```

The first build:

1. Checks the `requires` line of both files.
2. Builds `level.yaml`, a level file because it has `npcs`, `streaming` and `startPoints`. Its `package`,
   `DLC_MOD_MyMod/CookedPCConsole/BIOA_MYM01.pcc`, lands under `out` by its DLC folder:
   `C:\mymod-build\DLC_MOD_MyMod\CookedPCConsole\BIOA_MYM01.pcc`. The build creates the level, streams the freighter in
   a copy of `BIOA_FRE31`'s `BioTriggerStream_0`, copies the post-process, adds the start point and imports the guard.
   The level has no `MyModGuard_dlg` yet, so the result lists it in `missingConversations`.
3. Builds `MyModGuard_dlg.yaml`, a conversation file because it has `conversation`. The build copies the template into
   the level and writes the text into every talk file of the conversation.
4. Builds `level.yaml` again (`levelsAgain` in the result) and adds the Kismet: Shepard uses the guard, the
   conversation starts.

Run it again: the level reports no change, the guard and the two new lines are `found`, `levelsAgain` is empty, and the
level file keeps the same bytes.

The build copies each file to `%LOCALAPPDATA%\mele-mcp\backups` (or `MELE_BACKUP_DIR`) before it saves it. With `out`,
`levels.json` (the file MeleBridge reads) is left alone. Delete the out folder when you are done.

### Install it

These steps use tools tested on other mods. The example itself has not been installed or played.

1. In ME3Tweaks Mod Manager (M3): Tools, "Generate new DLC Mod Starter Kit", game LE1. Mod name `My Mod`, DLC folder
   suffix `MyMod` (the folder becomes `DLC_MOD_MyMod`), TLK ID `61230000`, and a mount priority from 11 to 31999 that
   no installed mod uses. The Starter Kit writes the mod into M3's library, with its `AutoLoad.ini` and GlobalTlk
   files.
2. To give the guard a name of his own, add two lines to `level.yaml` (otherwise he keeps the template's actor type,
   `nor10_navigator`). The GlobalTlk is the Starter Kit's, found in the level's folder:

   ```yaml
   globalTlk: [DLC_MOD_MyMod_GlobalTlk.pcc]   # after 'package'
   ```

   ```yaml
       name: { type: MyModGuard, strref: 61230001, en: "Cargo Guard" }   # in npcs.guard
   ```

3. Call `mod_build` with `folder` only. The `package` path now resolves to the library mod that has `DLC_MOD_MyMod`,
   and `level_build` writes the level's entry of `levels.json`.
4. Call `mod_check` with `mod` set to `My Mod` and `languages` set to `INT` (the name text above is English only).
5. Close the game, then call `mod_install` with `mod` set to `My Mod`.
6. In game, open the console and enter `at BIOA_MYM01 start_mym01`.

### Make it yours

- Rename everything a mod owns: the DLC folder, the level file, the conversation (its base name also names the
  `<base>_D` and `<base>_N` packages and `TlkSet_<base>`), the actor type in `name.type`, and the tags.
- Take your own block of ids. The example uses strings from `61230000`, resref `612301` and node ids from `61230100`.
  `id_alloc` finds free ids, or checks the ones you picked.
- Place the guard where Shepard stands: `npc_place` with `file` set to your `level.yaml`, while the game runs in that
  map. It writes `location` and `yaw` into the file and builds the level.
