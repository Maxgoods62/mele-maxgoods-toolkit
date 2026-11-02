# release-kit

One set of release files for the mods listed in `repos.conf`. `kit.sh` renders `templates/` into normal files in each repo,
so every repo stays self-contained: nothing reads this folder at run time, each repo signs its own builds, and no repo
needs another to build. The kit commits nothing.

```bash
kit.sh apply <folder>|all     # write the files into a checkout; refuses uncommitted changes in them
kit.sh check [<folder>|all]   # compare the render with the repo's main; KIT_REF=<branch> checks a branch
kit.sh scan [<folder>|all]    # game files in any commit, private strings in main's files; run before a repo goes public
```

`KIT_ROOT` is a folder that holds each repo at its `repos.conf` path. A failed `check` on a repo with no files yet just means
it isn't set up. `scan` reads private strings in the latest files only: old commits keep what they held.

## Profiles

| Profile | For | Rendered files |
|---|---|---|
| `hosted` | Mods GitHub's Windows runner can build | `.github/workflows/build.yml`, `docs/releasing.md`, `scripts/protect-repo.sh`, `scripts/release-notes.sh` |
| `local` | Mods whose build needs the game files, so no runner can build them | `scripts/protect-repo.sh` |

A `local` repo keeps its own `scripts/release.sh` and `docs/releasing.md`. It has no build provenance: a hash and an
immutable release show that a file didn't change after publishing, not what it was built from.

## What a hosted repo must provide

1. `./package.sh --label <latest|v<N>>` runs in Git Bash on `windows-2025` with only `MELE_LEXASIS` set. It uses no game
   files and no `build.env`.
2. It writes `dist/LE<n><stem>-<label>.zip`, with an LE1 zip among them, `dist/SHA256SUMS` over exactly those zips, and
   `dist/stage/*.pdb`.
3. It stops before building when a `v<N>` label differs from the version.
4. Tags are `v<N>`, with N equal to `ASI_VERSION` in `src/SharedVersion.h`.
5. `CHANGELOG.md` has exactly one `## v<N> (YYYY-MM-DD)` heading per tag, with text under it.
6. Scripts are LF and executable. `dist/` and `*.zip` are ignored.

`check` doesn't test this list: the first run does. `package.sh` and `build.sh` stay in each repo, and `check` can't see them
drift.

## Change a shared file

1. Edit the template and commit it in the toolkit.
2. `kit.sh apply mods/pause-anytime`, read the diff, commit, push, and wait for a green run. pause-anytime goes first every
   time.
3. Apply to the other repos, then commit and push each.
4. `kit.sh check` prints `ok` for every repo.

Published tags are never touched. A hand edit in a repo stays red in `check` until it is in the template. Nothing updates the
pinned `uses:` lines for you: bump them in the template.

## Add a repo

Add a tab-separated line to `repos.conf`. A title holds letters, digits, spaces and `. _ -`. Meet the list above, run
`kit.sh apply <folder>`, commit and push there, then follow that repo's `docs/releasing.md`, First setup.
