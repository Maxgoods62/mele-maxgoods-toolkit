#!/usr/bin/env bash
# Rendered from mele-maxgoods-toolkit tools/release-kit. Change it there; a hand edit here fails kit.sh check.
# Locks the GitHub repo for releases. Run it once, right after the repo goes public and before the first release tag:
# GitHub refuses rulesets and Pages on a free account's private repo, and immutability covers only the releases published
# after it is on. Safe to run again. Needs gh, logged in as the repo's admin.
#   scripts/protect-repo.sh [owner/repo]
# docs/releasing.md says what each setting stops.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO="${1:-@REPO@}"

# On a private repo GitHub takes the first setting and refuses the rest, and a test release then can't give its tag back.
if [[ "$(gh api "repos/$REPO" --jq .private)" != false ]]; then
    echo "protect-repo.sh: $REPO is not public: make it public first" >&2
    exit 1
fi

gh api -X PUT "repos/$REPO/immutable-releases" > /dev/null
echo "immutable releases: on"

gh api -X PUT "repos/$REPO/actions/permissions" -F enabled=true -f allowed_actions=selected \
    -F sha_pinning_required=true > /dev/null
gh api -X PUT "repos/$REPO/actions/permissions/selected-actions" -F github_owned_allowed=true \
    -F verified_allowed=false > /dev/null
gh api -X PUT "repos/$REPO/actions/permissions/fork-pr-contributor-approval" \
    -f approval_policy=all_external_contributors > /dev/null
echo "actions: GitHub's own only, pinned by commit; a fork's pull request needs approval to run"

# Creates the ruleset named $1, or replaces it, from the JSON on stdin.
ruleset() {
    local id
    id="$(gh api "repos/$REPO/rulesets" --jq ".[] | select(.name == \"$1\") | .id")"
    if [[ -n "$id" ]]; then
        gh api -X PUT "repos/$REPO/rulesets/$id" --input - > /dev/null
    else
        gh api -X POST "repos/$REPO/rulesets" --input - > /dev/null
    fi
    echo "ruleset: $1"
}

# No bypass: these hold for the admin too. Direct pushes to main stay allowed.
ruleset main <<'EOF'
{
  "name": "main",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "rules": [ { "type": "deletion" }, { "type": "non_fast_forward" } ]
}
EOF

ruleset 'release tags are final' <<'EOF'
{
  "name": "release tags are final",
  "target": "tag",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["refs/tags/v*"], "exclude": [] } },
  "rules": [ { "type": "update" }, { "type": "deletion" }, { "type": "non_fast_forward" } ]
}
EOF

# A separate ruleset, because a bypass covers its whole ruleset: here it lets the admin create a tag, nothing more.
ruleset 'only admins create release tags' <<'EOF'
{
  "name": "only admins create release tags",
  "target": "tag",
  "enforcement": "active",
  "bypass_actors": [ { "actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always" } ],
  "conditions": { "ref_name": { "include": ["refs/tags/v*"], "exclude": [] } },
  "rules": [ { "type": "creation" } ]
}
EOF

# Only a repo with this workflow publishes a latest page.
if [[ -f "$HERE/.github/workflows/build.yml" ]]; then
    gh api "repos/$REPO/pages" > /dev/null 2>&1 || gh api -X POST "repos/$REPO/pages" -f build_type=workflow > /dev/null
    echo "pages: published by the workflow"
fi
