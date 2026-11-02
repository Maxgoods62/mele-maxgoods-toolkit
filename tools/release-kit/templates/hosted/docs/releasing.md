# Releasing @TITLE@

<!-- Rendered from mele-maxgoods-toolkit tools/release-kit. Change it there; a hand edit here fails kit.sh check. -->

[`.github/workflows/build.yml`](../.github/workflows/build.yml) builds and publishes @TITLE@ on GitHub's Windows runner,
with the same `package.sh` and `build.sh` you run at home.

| Push | Build | Published at |
|---|---|---|
| `main` | `@NAME@-latest.zip`, `SHA256SUMS` | <@PAGES@latest/>, replaced by each push |
| tag `v<N>` | `@NAME@-v<N>.zip`, `SHA256SUMS` | [Releases](https://github.com/@REPO@/releases), never replaced |

The zip holds one `README.txt`, a folder per game with that game's files (`LE1`, `LE2`, `LE3`; a mod for one game has none)
and a `LICENSE` folder with the license texts. Each run's summary shows the hashes. The run also keeps the `.pdb` files
for 90 days, as the `pdb` artifact: only these match that build's ASI.

## First setup

Run this once, right after the repo goes public and before the first tag:

```bash
scripts/protect-repo.sh
```

While the repo is private, a tag publishes a private release with no attestation, and `main` publishes no latest page.
Delete a test release before the repo goes public: `gh release delete v<N> --cleanup-tag`. Once `protect-repo.sh` has run,
a tag name can't be used twice.

## Publish a version

N is `ASI_VERSION`.

1. In the toolkit, run `tools/release-kit/kit.sh check`.
2. Set `ASI_VERSION` and its two strings in `src/SharedVersion.h`, and date its entry in `CHANGELOG.md`:
   `## v<N> (YYYY-MM-DD)`.
3. Push `main`, and test the zip from the latest page in the game.
4. Tag that commit and push the tag:

   ```bash
   scripts/release-notes.sh --check v<N>
   git tag -a v<N> -m "@TITLE@ v<N>"
   git push origin v<N>
   ```

The workflow stops, and publishes nothing, when the tag differs from `ASI_VERSION`, when the commit is not on `main`, when
`CHANGELOG.md` has no dated entry for it, or when the tag already has a release. Once `protect-repo.sh` has run, a pushed tag
can't be moved or deleted, even one the workflow refused: a bad tag is fixed by the next number.

## Check a download

With the [GitHub CLI](https://cli.github.com), signed in to a free GitHub account (`gh auth login`), check that the file is
that release's, then that this repo's workflow built it from that tag:

```bash
gh release verify-asset v<N> @NAME@-v<N>.zip -R @REPO@
gh attestation verify @NAME@-v<N>.zip -R @REPO@ --source-ref refs/tags/v<N>
```

On a latest zip, `gh attestation verify` works without `--source-ref`.

Without the CLI, compare the SHA-256 hash in PowerShell with `SHA256SUMS`. It shows that the file matches the list:

```powershell
Get-FileHash .\@NAME@-v<N>.zip -Algorithm SHA256
```

## What stops a rebuilt tag

| Setting | Stops |
|---|---|
| Immutable releases | Changing or deleting a published release's files or moving its tag. If the release itself is deleted, its tag name can't be used again. |
| Ruleset `release tags are final` | Moving or deleting a `v*` tag, for everyone, the admin included. |
| Ruleset `only admins create release tags` | A collaborator creating a `v*` tag. |
| The release job's first step, then `gh release create` | Re-running a tag's workflow: it refuses a tag that has a published release, so a re-run signs and publishes nothing. |
| Attestation | A file swapped outside the workflow going unnoticed: the signature, in a public log, is tied to the file's hash. |
| Ruleset `main` | Force pushes to `main` and deleting it. Direct pushes stay allowed. |
| Actions policy | Any action not from GitHub or not pinned to a commit, and a fork's pull request running a workflow of its own without your approval. |

Pull requests don't start this workflow, and the build job has no write permission and no cache. Keep the `uses:` lines
pinned to full commits when you update them.
