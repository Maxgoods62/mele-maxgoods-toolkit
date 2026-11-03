# Releasing @TITLE@

<!-- Rendered from mele-maxgoods-toolkit tools/release-kit. Change it there; a hand edit here fails kit.sh check. -->

[`.github/workflows/build.yml`](../.github/workflows/build.yml) builds and publishes @TITLE@
on GitHub's Windows runner, with the same `package.sh` and `build.sh` you run at home.

| Push | Build | Published at |
|---|---|---|
| `main` | `@NAME@-latest.zip`, `SHA256SUMS` | <@PAGES@latest/>, replaced by each push |
| tag `v<N>` | `@NAME@-v<N>.zip`, `SHA256SUMS` | [Releases](https://github.com/@REPO@/releases), never replaced |

The zip holds one `README.txt`, a folder per game with that game's files (`LE1`, `LE2`, `LE3`), and a `LICENSE` folder
with the license texts. (A mod made for one game only has its files at the root of the zip, with no game folder.) Each
run's summary shows the hashes. The run also keeps the `.pdb` files for 90 days, as the `pdb` artifact. A `.pdb` matches
only the ASI built in the same run.

## First setup

Run this once, right after the repo goes public and before the first tag:

```bash
scripts/protect-repo.sh
```

While the repo is private, a tag publishes a private release with no attestation, and `main` publishes no latest page.
Delete a test release before the repo goes public: `gh release delete v<N> --cleanup-tag`.
Once `protect-repo.sh` has run, a tag name can't be used twice.

## Publish a version

N is `ASI_VERSION`.

1. In [mele-maxgoods-toolkit](https://github.com/Maxgoods62/mele-maxgoods-toolkit), run
   `tools/release-kit/kit.sh check`. It checks that each repo's release files on `main` match the kit's templates.
2. Set `ASI_VERSION` and its two strings in `src/SharedVersion.h`, and date its entry in `CHANGELOG.md`:
   `## v<N> (YYYY-MM-DD)`.
3. Push `main`, and test the zip from the latest page in the game.
4. Tag that commit and push the tag:

   ```bash
   scripts/release-notes.sh --check v<N>
   git tag -a v<N> -m "@TITLE@ v<N>"
   git push origin v<N>
   ```

The workflow stops, and publishes nothing, when:

- the tag differs from `ASI_VERSION`;
- the commit is not on `main`;
- `CHANGELOG.md` has no dated entry for that version;
- the tag already has a release.

Once `protect-repo.sh` has run, a pushed tag can't be moved or deleted, even one the workflow refused. To fix a bad tag,
release the next version number instead.

## Check a download

`SHA256SUMS` is next to the zip, on the release or on the latest page. In PowerShell, compute the zip's SHA-256 hash and
compare it with the zip's line in `SHA256SUMS`. Upper or lower case does not matter. This check shows only that the file
matches the list:

```powershell
Get-FileHash .\@NAME@-v<N>.zip -Algorithm SHA256
```

The [GitHub CLI](https://cli.github.com) checks more. First sign in to a free GitHub account with `gh auth login`. The
first command below checks that the file belongs to that release. The second checks that this repo's workflow built it
from that tag:

```bash
gh release verify-asset v<N> @NAME@-v<N>.zip -R @REPO@
gh attestation verify @NAME@-v<N>.zip -R @REPO@ --source-ref refs/tags/v<N> \
  --signer-workflow @REPO@/.github/workflows/build.yml
```

A latest zip is built from `main`:

```bash
gh attestation verify @NAME@-latest.zip -R @REPO@ --source-ref refs/heads/main \
  --signer-workflow @REPO@/.github/workflows/build.yml
```

Keep both flags. Without `--source-ref`, a run on another branch or tag can sign a zip that passes. Without
`--signer-workflow`, another workflow file in this repo can.

## What stops a rebuilt tag

| Setting | What it does |
|---|---|
| Immutable releases | A published release's files can't be changed or deleted, and its tag can't move. |
| Ruleset `release tags are final` | No one can move or delete a `v*` tag, the admin included. |
| Ruleset `only admins create release tags` | A collaborator can't create a `v*` tag. |
| The release job's first step, then `gh release create` | Both refuse a tag that already has a published release. |
| Attestation | A file replaced outside the workflow can't pass unnoticed. |
| Ruleset `main` | No one can force-push to `main` or delete it. Direct pushes stay allowed. |
| Actions policy | Only actions from GitHub, pinned to a commit, can run. |

- If an immutable release is deleted, its tag name can't be used again.
- A re-run of a tag's workflow signs and publishes nothing, because the tag already has its release.
- The attestation's signature is stored in a public log and tied to the file's hash.
- Under the Actions policy, a fork's pull request can't run a workflow of its own without your approval.

Pull requests don't start this workflow, and the build job has no write permission and no cache. Keep the `uses:` lines
pinned to full commits when you update them.
