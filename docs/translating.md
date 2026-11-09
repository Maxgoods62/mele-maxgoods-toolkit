# Translating my mods

Every word my public mods show in game has a string id, so you can translate a mod without changing it. A translation
is your own mod for ME3Tweaks Mod Manager (M3): you ship it, and my mods never include your text. Each mod's
`docs/translating.md` lists its string ids and says which of the two ways below it takes. This page says how to build
the translation mod.

## Two ways

| | A mod with talk files | An ASI mod |
|---|---|---|
| Example | [Galaxy Map Normandy](https://github.com/Maxgoods62/mele-galaxy-map-normandy/blob/main/docs/translating.md) | [Pause Anytime](https://github.com/Maxgoods62/mele-pause-anytime/blob/main/docs/translating.md) |
| Its words | in its own talk files, one file per language code | built into the ASI, which asks the game for its ids |
| Your mod holds | copies of the mod's files for your language, under the same names | talk files of your own that define the mod's ids |
| Your mount | above the mod's (its page gives it), and used by no other mod | any mount no other mod uses |

For a mod with talk files, your file replaces the mod's file whole, so the mod's text for an id you leave out is gone.
Start from the mod's file and change only the strings you translate.

For an ASI mod, an id you leave out shows what the mod shows without a translation (its page says). Put the mod's ids
only in the files of the languages you translate: an id in any other file replaces the mod's own words in that
language.

## LE1

### The DLC folder

Make a folder `DLC_MOD_<Name>`, where `<Name>` is your mod's name without spaces. M3's Starter Kit can make it. Its
`AutoLoad.ini` gives the mount:

```ini
[ME1DLCMOUNT]
ModName=<Name>
ModMount=<mount>
```

When two DLC folders hold a file of the same name, LE1 loads the copy from the higher mount. Only a higher mount has
been tried: with a lower one, the mod's own file would most likely win, and nothing would say so.

A translation of a mod with talk files needs nothing more: the mod's own `AutoLoad.ini` already names its packages. For
an ASI mod, your `AutoLoad.ini` also names your talk files:

```ini
[Packages]
GlobalTalkTable1=DLC_MOD_<Name>_GlobalTlk.GlobalTlk_tlk
```

Galaxy Map Normandy's `mod/DLC_MOD_GalaxyMapNormandy/AutoLoad.ini` names its own talk files this way.

LE1 needs an ASI to load DLC mods. M3 installs it with your mod, so you ship nothing for it.

### The talk files

Talk files go in `CookedPCConsole`, in one GlobalTlk package per language code. The English package has no code in
its name, and every other package ends in `_<code>`: `DLC_MOD_<Name>_GlobalTlk.pcc`,
`DLC_MOD_<Name>_GlobalTlk_FR.pcc`. For a mod with talk files, use the mod's own package names instead (its page lists
them), and ship only the files of your language.

| Language | Codes |
|---|---|
| English | none |
| French | `FR`, `FE` |
| German | `DE`, `GE` |
| Italian | `IT`, `IE` |
| Spanish | `ES` |
| Polish | `PL`, `PLPC` |
| Russian | `RU`, `RA` |
| Japanese | `JA` |

The two codes of a language show the same text: one is for a game with that language's voices, the other for English
voices. Ship both, with the same text.

Each package holds two talk tables, `GlobalTlk_tlk` and `GlobalTlk_tlk_M`, as LE1's own packages do: one for a female
Shepard, the other for a male one. Put every id in both.

LE1 reads the English package for a language your mod has no package for (tested with French). So for an ASI mod:

- Ship the English package, which `AutoLoad.ini` names, and the packages of the languages you translate.
- Unless you translate English, the English package holds none of the mod's ids, only a string of your own, such as
  your mod's name under your mount times 10000 (mount 7481 gives 74810000), as my mods do.
- If you do translate English, your English words also show in every language you ship no package for.

Legendary Explorer is enough to edit the talk tables of a package. The toolkit's `lectool tlkpackage`
([lectool](../tools/lectool/README.md#strings)) can also write a package with both tables from a text file, one line
per id (the id, a tab, the text). It refuses a file with no string, so give each file at least one line.

## LE2 and LE3

Only the ASI way applies here so far: none of my public mods for LE2 or LE3 has talk files.

Start from a DLC folder made by M3's Starter Kit, or by `dlc_new`, a tool of the toolkit's MCP server
([mele-mcp](../tools/mele-mcp/README.md#builds-and-mods)). Either makes every file the game needs to mount the DLC,
such as `Mount.dlc`, LE2's `BIOEngine.ini` and LE3's `Default_DLC_MOD_<Name>.bin`, and a talk file per language. Keep
them all, and add the mod's ids to the talk files of the languages you translate, in
`DLC_MOD_<Name>\CookedPCConsole`:

- LE2: `DLC_<module>_<code>.tlk`, where `<module>` is the number that `BIOEngine.ini` gives the DLC under
  `[Engine.DLCModules]`;
- LE3: `DLC_MOD_<Name>_<code>.tlk`.

The codes are `INT` (English), `DEU`, `ESN`, `FRA`, `ITA`, `JPN`, `POL` and `RUS`. You need no `FRE`, `DEE`, `ITE` or
`POE` file: no LE2 or LE3 folder ships one, so a game over English voices should read the `FRA`, `DEU`, `ITA` or `POL`
file (not tried with a translation mod).

A `.tlk` file has a main table, and a second table for lines that change with a female Shepard. The mod's words go in
the main table. Legendary Explorer's TLK editor edits `.tlk` files, and so does `tlk_set`, a tool of the toolkit's MCP
server ([mele-mcp](../tools/mele-mcp/README.md#text)).

## Check it in the game

1. Close the game, and install your mod with M3. The mod you translate stays installed.
2. Start the game in your language: the text language picked in the game's launcher.
3. Look where the mod's page says each string shows. Its page also names the log lines to look for, if any.

After each change, close the game and install your mod again with M3.

## What has been tested

On 2026-11-09, in LE1, with translation mods built by the steps on this page (then on each mod's own page):

- A copy of Galaxy Map Normandy's English package at mount 7480 replaced its words in an English game. A French game
  kept the mod's own French package, since the copy had no French one.
- A translation mod's own GlobalTlk package at mount 7481, named in its `AutoLoad.ini`, defined Pause Anytime's ids.
  With the ids only in its English package, the pause screen showed the translation's words in an English game and in
  a French game.
- With only its own name (id 74810000) in its English package and Pause Anytime's ids in its `FR` and `FE` packages, a
  French game showed the translation's words and an English game Pause Anytime's own.
- `lectool tlkpackage` on a file with no string stopped with `no strings`.

The same day, in LE2 and LE3, copies of another of my mods' `.tlk` files defined Pause Anytime's ids in an English
game, and the pause screen showed their words. That was not a mod built from this page.

Still untested: two translation mods for the same language or the same id, a lower mount, a translation of a mod with
talk files that defines its ids in packages of other names, a translation mod built from this page in LE2 or LE3,
LE1's English fallback in languages other than French, and a game over English voices with a translation mod.
