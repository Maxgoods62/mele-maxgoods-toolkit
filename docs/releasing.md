# Releasing my mods

My public mods share one way to release, and their READMEs link to this page. A mod built on GitHub follows the steps
in [Publish a version](#publish-a-version). A mod built on my PC has its steps at the top of its `scripts/release.sh`.

## Two ways to build

A mod whose build needs no game files, such as an ASI mod, is built on GitHub. A mod whose build reads the game files
is built on my PC.

| | Built on GitHub | Built on my PC |
|---|---|---|
| Who builds it | GitHub's Windows runner, from the tagged commit | Me, with the scripts in the mod's repo |
| You download | A zip from the repo's Releases page | A 7z from Nexus Mods |
| The GitHub release holds | The zip and `SHA256SUMS` | `SHA256SUMS` only, with the hash of the 7z |
| Proof of the build | A signed record of the workflow and commit that built it | None |

The 7z of a mod built on my PC holds content made from the games, so I don't put it on GitHub. Its release notes name
the mod commit and the toolkit commit it was built with. That toolkit commit must be on `main` in the public toolkit
repo.

A mod built on GitHub also has a latest build. Each push to `main` builds a zip and puts it on the repo's GitHub Pages
site: `https://<owner>.github.io/<repo>/latest/`, all in lower case. The page names the commit it was built from. A
latest build has less testing than a release, and the next push replaces it.

## What every release has

- A tag that names the version, such as `v9` or `v0.27`. The mod's version number must match it.
- One dated entry in `CHANGELOG.md`. Without it, the release stops.
- A `README.txt` and a `LICENSE` folder in the archive. A zip built on GitHub for several games has one folder per
  game: `LE1`, `LE2`, `LE3`. A 7z keeps the layout that ME3Tweaks Mod Manager gives it.
- No game files and no built binaries in git. Before a repo goes public, `kit.sh scan` from the
  [release kit](../tools/release-kit/README.md) checks the file names in every commit for game files and for `.asi`,
  `.dll` and `.exe` files.

## Repo settings

Each repo runs `scripts/protect-repo.sh` once, when it goes public and before its first release tag. It turns on:

| Setting | What it does |
|---|---|
| Immutable releases | No one can change or delete a published release's files, or move its tag. A deleted release's tag name can't be used again. |
| Ruleset `release tags are final` | No one can move or delete a `v*` tag. It has no exception for me: I would have to turn the ruleset off. |
| Ruleset `only admins create release tags` | Only the repo's admin can create a `v*` tag. |
| Ruleset `main` | No one can force-push to `main` or delete it. I still push to it directly. |
| Actions policy | Workflows can use only GitHub's actions and my own. Actions from other repos must be pinned to a full commit. A pull request from a fork runs no workflow until I approve it. |

While a repo is still private, I push a tag only to test the workflow. That tag publishes a private release with no
signed record, and a push to `main` publishes no latest page. I delete each test release before the repo goes public:
`gh release delete v<N> --cleanup-tag`.

## Publish a version

These are my steps for a mod built on GitHub. `<N>` is the mod's version number, the value of `ASI_VERSION` in its
`src/SharedVersion.h`. If a mod needs more for a release, its `BUILDING.md` says so.

1. In the toolkit, run `tools/release-kit/kit.sh check`. It checks that each mod's release files on `main` match the
   release kit.
2. Set `ASI_VERSION`, `VERSION_STRING_A` and `VERSION_STRING_W` in `src/SharedVersion.h` to the new number, and date
   the version's entry in `CHANGELOG.md`: `## v<N> (YYYY-MM-DD)`.
3. Push `main`. Download the zip from the latest page and test it in the game. While the repo is private, take the zip
   from the run's `dist` artifact instead.
4. Check the `CHANGELOG.md` entry, then tag that commit and push the tag:

   ```bash
   scripts/release-notes.sh --check v<N>
   git tag -a v<N> -m "<mod name> v<N>"
   git push origin v<N>
   ```

The workflow then builds the zip from the tag and publishes the release. Each run's summary shows the hashes. The run
also keeps the `.pdb` files for 90 days, as its `pdb` artifact. A `.pdb` matches only the ASI built in the same run.

The workflow refuses the tag, and publishes nothing, when the tag differs from `ASI_VERSION`, its commit is not on
`main`, `CHANGELOG.md` has no dated entry for it, or it already has a release. Once `protect-repo.sh` has run, a pushed
tag can't be moved or deleted, even one the workflow refused. To fix a bad tag, I release the next version number.

## How the GitHub build is protected

Each repo's `.github/workflows/build.yml` comes from the release kit.

- It starts only on a push to `main` or a `v<N>` tag. Pull requests don't start it.
- The build job has a read-only token, keeps no git credentials and uses no cache. It runs the same `build.sh` and
  `package.sh` I run at home.
- Every action and every library that `build.sh` downloads is pinned to a full commit. The compiler and the other tools
  come with GitHub's Windows image, which GitHub updates.
- A tag that is not on `main`, or that already has a release, publishes nothing.
- The jobs that publish don't check out the repo, so they run none of its scripts. They check the zips against the
  build's hashes and sign where the zips came from. The release job then publishes, downloads what it published, and
  checks it again.

## Check a download

Compute the file's hash in PowerShell:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath "<file>").Hash
```

Compare it with the file's line in `SHA256SUMS`: on the GitHub release whose tag is the mod's version, or on the latest
page for a latest zip. Upper or lower case does not matter. Nexus renames the files you download, so compare the hash,
not the name. The release notes copy the hashes. If they ever differ from `SHA256SUMS`, trust the file: notes can be
edited, the file can't.

This shows only that your file matches the list. The [GitHub CLI](https://cli.github.com) checks more. Sign in once
with `gh auth login`; a free account is enough. In the commands below, `<owner>/<repo>` is the part of the repo's URL
after `github.com/`, typed with the same upper and lower case. `<tag>` is the release tag, and `<file>` is the file you
downloaded.

For a mod built on GitHub, the first command checks that the zip belongs to the release. The second checks that the
repo's workflow built it from that tag:

```
gh release verify-asset <tag> <file> -R <owner>/<repo>
gh attestation verify <file> -R <owner>/<repo> --source-ref refs/tags/<tag> --signer-workflow <owner>/<repo>/.github/workflows/build.yml
```

For a latest zip, run only the second command, with `--source-ref refs/heads/main`. Keep both flags. Without
`--source-ref`, a zip built from another branch or tag passes. Without `--signer-workflow`, a zip signed by another
workflow in the repo passes.

For a mod from Nexus, check that the `SHA256SUMS` you compared with is the release's own. If you don't have that file
yet, download it first with the first command:

```
gh release download <tag> -R <owner>/<repo> -p SHA256SUMS
gh release verify-asset <tag> SHA256SUMS -R <owner>/<repo>
```

## What the checks don't prove

- They show where a file came from, which says nothing about bugs or safety. An ASI runs inside the game with the
  game's access. To know what a mod does, read its source.
- A mod built on my PC has no proof of its build. The hash shows that the Nexus file is the one I published. It doesn't
  show that the file was built from the tag, or that my PC and game files were clean. A rebuild can't check it either,
  because two builds of the same source never give the same bytes.
- Someone who takes over my GitHub account could publish a new version, replace the latest build, edit release notes
  or delete a release. They couldn't change the files of a release that is already out, or use its tag for other
  files.
