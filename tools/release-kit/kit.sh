#!/usr/bin/env bash
# Renders templates/ into the repos of repos.conf. Commits nothing, talks to no server.
#   kit.sh apply <folder>|all      write the files into the checkout
#   kit.sh check [<folder>|all]    compare the render with the repo's $KIT_REF (default main); changes nothing
#   kit.sh scan [<folder>|all]     game files in any commit, private strings in $KIT_REF's files; changes nothing
# Folders are relative to $KIT_ROOT (default: the folder that holds the toolkit).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="${KIT_ROOT:-$HERE/../../..}"
REF="${KIT_REF:-main}"
GAME_FILES='\.(pcc|tfc|bik|isb|afc|wav|ogg|wem|tlk|sav|dll|asi|exe)$'
PRIVATE='ai-docs|mcp__|/home/[a-z]|/mnt/[d-z]/|[D-Z]:\\[A-Za-z]|@gmail'
HEADER='Rendered from mele-maxgoods-toolkit tools/release-kit'

die() { echo "kit.sh: $*" >&2; exit 1; }

# load <folder> sets folder, repo, name, title, profile, pages and dir.
load() {
    local line
    line="$(awk -F'\t' -v f="$1" '$1 == f' "$HERE/repos.conf")"
    [[ -n "$line" ]] || die "$1 is not in repos.conf"
    IFS=$'\t' read -r folder repo name title profile <<< "$line"
    case "$profile" in
        hosted|local) ;;
        *) die "$folder: unknown profile '$profile'" ;;
    esac
    [[ -d "$HERE/templates/all" && ( "$profile" == local || -d "$HERE/templates/$profile" ) ]] || die "templates are missing"
    # The title goes into sed, shell and YAML.
    [[ "$title" =~ ^[A-Za-z0-9][A-Za-z0-9\ ._-]*$ ]] || die "$folder: title has characters the templates can't carry"
    pages="https://${repo%%/*}.github.io/${repo#*/}/"
    pages="${pages,,}"
    dir="$ROOT/$folder"
    [[ "$(git -C "$dir" rev-parse --show-toplevel 2> /dev/null)" -ef "$dir" ]] || die "no git repo at $dir"
}

# Prints "<profile folder><tab><path>" for each template of this repo: a file's profile is the folder it sits in.
files() {
    local base
    for base in all "$profile"; do
        [[ -d "$HERE/templates/$base" ]] || continue
        (cd "$HERE/templates/$base" && find . -type f | sort | sed "s|^\./||; s|^|$base\t|")
    done
}

render() {
    sed -e "s|@PAGES@|$pages|g" -e "s|@REPO@|$repo|g" -e "s|@NAME@|$name|g" -e "s|@TITLE@|$title|g" "$HERE/templates/$1"
}

apply() {
    load "$1"
    local base path text
    # Every file is tested before the first one is written, so a refusal leaves the repo as it was.
    while IFS=$'\t' read -r base path; do
        [[ -z "$(git -C "$dir" status --porcelain -- "$path")" ]] || die "$folder/$path has uncommitted changes"
        text="$(render "$base/$path")"
        ! grep -nE '@[A-Z]+@' <<< "$text" || die "$path: token left"
        ! grep -nE "$PRIVATE" <<< "$text" || die "$path: private string"
    done < <(files)
    while IFS=$'\t' read -r base path; do
        mkdir -p "$dir/$(dirname "$path")"
        render "$base/$path" > "$dir/$path"
        if [[ "$path" == *.sh ]]; then chmod +x "$dir/$path"; fi
        echo "wrote $folder/$path"
    done < <(files)
}

check() {
    load "$1"
    local base path mode wanted bad=0
    while IFS=$'\t' read -r base path; do
        mode=100644
        if [[ "$path" == *.sh ]]; then mode=100755; fi
        if ! git -C "$dir" cat-file -e "$REF:$path" 2> /dev/null; then
            echo "missing: $folder/$path in $REF"
            bad=1
        elif ! diff -u --label "$REF:$path" --label rendered <(git -C "$dir" show "$REF:$path") <(render "$base/$path"); then
            bad=1
        elif [[ "$(git -C "$dir" ls-tree "$REF" "$path" | cut -c1-6)" != "$mode" ]]; then
            echo "mode: $folder/$path is not $mode in $REF"
            bad=1
        fi
    done < <(files)
    # A rendered file whose template is gone stays live in the repo: a second workflow would still run.
    wanted="$(files | cut -f2)"
    while IFS= read -r path; do
        if ! grep -qxF "$path" <<< "$wanted"; then
            echo "orphan: $folder/$path has no template"
            bad=1
        fi
    done < <(git -C "$dir" grep -lF "$HEADER" "$REF" -- . 2> /dev/null | cut -d: -f2-)
    if (( bad == 0 )); then echo "ok $folder ($REF)"; fi
    return "$bad"
}

scan() {
    load "$1"
    local rc=0 bad=0
    git -C "$dir" rev-parse --verify -q "$REF^{commit}" > /dev/null || die "no $REF in $folder"
    if git -C "$dir" log --all -m --name-only --format= -z | tr '\0' '\n' | sort -u | grep -iE "$GAME_FILES"; then bad=1; fi
    git -C "$dir" grep -nIE "$PRIVATE" "$REF" -- . || rc=$?
    case "$rc" in
        0) bad=1 ;;
        1) ;;
        *) die "git grep failed in $folder" ;;
    esac
    if (( bad == 0 )); then echo "clean $folder ($REF)"; fi
    return "$bad"
}

each() {
    local rc=0 f rows
    rows="$(grep -v '^#' "$HERE/repos.conf" | cut -f1 || true)"
    [[ -n "$rows" ]] || die "no repos in repos.conf"
    # A new process for each: a function called before || runs with set -e off.
    for f in $rows; do
        bash "$HERE/kit.sh" "$1" "$f" || rc=1
    done
    return "$rc"
}

USAGE="usage: kit.sh apply <folder>|all, or kit.sh check|scan [<folder>|all]"
cmd="${1:-}"
case "$cmd" in
    apply) (( $# == 2 )) || die "$USAGE" ;;
    check|scan) (( $# <= 2 )) || die "$USAGE" ;;
    *) die "$USAGE" ;;
esac
target="${2-all}"
if [[ "$target" == all ]]; then each "$cmd"; else "$cmd" "$target"; fi
