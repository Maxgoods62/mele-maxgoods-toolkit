#!/usr/bin/env bash
# Rendered from mele-maxgoods-toolkit tools/release-kit. Change it there; a hand edit here fails kit.sh check.
# Prints the notes of GitHub release v<N>: that version's CHANGELOG.md entry, how to check a download, and the hashes in
# dist/SHA256SUMS. With --check it only tests the CHANGELOG.md entry.
#   scripts/release-notes.sh v18 > dist/release-notes.md
#   scripts/release-notes.sh --check v18
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CHECK_ONLY=""
if [[ "${1:-}" == --check ]]; then CHECK_ONLY=1; shift; fi
TAG="${1:?usage: scripts/release-notes.sh [--check] v<N>}"
die() { echo "release-notes.sh: $*" >&2; exit 1; }

HEADS="$(grep -c "^## $TAG " "$HERE/CHANGELOG.md" || true)"
[[ "$HEADS" == 1 ]] || die "CHANGELOG.md needs exactly one '## $TAG ' heading, it has $HEADS"
grep -qE "^## $TAG \([0-9]{4}-[0-9]{2}-[0-9]{2}\)" "$HERE/CHANGELOG.md" \
    || die "the $TAG heading in CHANGELOG.md has no (YYYY-MM-DD) date"
ENTRY="$(awk -v head="## $TAG " 'index($0, head) == 1 { on = 1; next } /^## / { on = 0 } on' "$HERE/CHANGELOG.md")"
[[ -n "${ENTRY//[$'\n ']/}" ]] || die "the $TAG entry in CHANGELOG.md is empty"
if [[ -n "$CHECK_ONLY" ]]; then exit 0; fi

[[ -s "$HERE/dist/SHA256SUMS" ]] || die "no dist/SHA256SUMS: run package.sh first"

printf '%s\n\n' "$ENTRY"
cat <<EOF
## Check your download

With the [GitHub CLI](https://cli.github.com), signed in to a free GitHub account (\`gh auth login\`), check that the file is
this release's, then that this repo's workflow built it from this tag:

\`\`\`
gh release verify-asset $TAG @NAME@-$TAG.zip -R @REPO@
gh attestation verify @NAME@-$TAG.zip -R @REPO@ --source-ref refs/tags/$TAG
\`\`\`

Or compare the hash in PowerShell with the table below. The table is a copy of this release's \`SHA256SUMS\`, which can't
change after publishing; these notes can.

\`\`\`powershell
Get-FileHash .\\@NAME@-$TAG.zip -Algorithm SHA256
\`\`\`

| File | SHA-256 |
|---|---|
EOF
while read -r hash name; do
    printf '| `%s` | `%s` |\n' "${name#\*}" "$hash"
done < "$HERE/dist/SHA256SUMS"
