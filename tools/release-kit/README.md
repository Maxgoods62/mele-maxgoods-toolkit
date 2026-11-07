# release-kit

This folder holds one set of release files for the mods listed in `repos.conf`. `kit.sh` renders `templates/` into plain
files in each mod repo and commits nothing. Nothing reads this folder at run time, and no repo needs another to build.
[docs/releasing.md](../../docs/releasing.md) explains the result for players and modders, and holds the release steps
of a hosted repo. Each mod's README links to it; no mod has a releasing page of its own.

## Commands

| Command | What it does |
|---|---|
| `kit.sh apply <folder>\|all` | Writes the files into a checkout. It renders and tests every file first and refuses uncommitted changes in them, a token left in a render, or a private string. A file that already holds its render doesn't count as changed, so a run that stopped partway can run again. |
| `kit.sh check [<folder>\|all]` | Compares the render with the repo's `KIT_REF`. It reports missing files, differences, wrong file modes and rendered files whose template is gone. |
| `kit.sh scan [<folder>\|all]` | Looks for game files in every commit, and for private strings in `KIT_REF`'s files. Run it before a repo goes public. |

`<folder>` is the first column of `repos.conf`. `check` and `scan` change nothing and default to `all`.

| Variable | Meaning | Default |
|---|---|---|
| `KIT_ROOT` | Folder holding each repo at its `repos.conf` path | The toolkit's parent folder |
| `KIT_REF` | Git ref that `check` and `scan` read | `main` |

`missing:` lines on a repo with no files yet only mean it is not set up. `scan` skips private strings in old commits, so
those commits keep what they hold.

## Profiles

| Profile | For | Rendered files |
|---|---|---|
| `hosted` | Mods GitHub's Windows runner can build | `.github/workflows/build.yml`, `scripts/protect-repo.sh`, `scripts/release-notes.sh` |
| `local` | Mods whose build needs game files | `scripts/protect-repo.sh` |

A `local` repo needs its own release script, with its steps at the top of the script. It has no build provenance: a
hash and an immutable release show only that a file stayed the same after publishing.

## What a hosted repo must provide

1. `./package.sh --label <latest|v<N>>` runs in Git Bash on `windows-2025` with only `MELE_LEXASIS` set. It uses no game
   files and no `build.env`.
2. It writes one zip, `dist/<stem>-<label>.zip`, then `dist/SHA256SUMS` over that zip, and `dist/stage/*.pdb`. The zip
   holds one `README.txt`, a `LICENSE` folder with the license texts, and the mod's files. Those sit in game folders
   (`LE1/`, `LE2/`, `LE3/`) for a multi-game mod, and at the root otherwise.
3. It stops before building when a `v<N>` label differs from the version or leaves out a released game.
4. Tags are `v<N>` or `v<N>.<M>`, equal to `v` and the version in `src/SharedVersion.h`.
5. `CHANGELOG.md` has exactly one `## v<N> (YYYY-MM-DD)` heading per tag, with text under it.
6. Scripts are LF and executable. `dist/` and `*.zip` are ignored.

`check` does not test this list or see `package.sh` and `build.sh` drift. The first run tests them.

## Change a shared file

1. Edit the template and commit it in the toolkit.
2. Run `kit.sh apply mods/pause-anytime` first, read the diff, commit, push and wait for a green run.
3. Apply to the other repos, then commit and push each.
4. Run `kit.sh check`. It prints `ok` for every repo.

A hand edit in a repo fails `check` until the template has it. Nothing updates the pinned `uses:` lines for you, so bump
them in the template.

## Add a repo

Add a tab-separated line to `repos.conf`: folder, owner/repo, file stem, title, profile. A title starts with a letter or
digit and holds letters, digits, spaces and `. _ -`. Meet the list above and run `kit.sh apply <folder>`. Commit and push
there. When the repo goes public, run its `scripts/protect-repo.sh` once, before its first release tag.
