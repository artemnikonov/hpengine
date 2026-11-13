#!/bin/sh
# Whitespace style ratchet for C# (rules: .editorconfig). The tree is NOT reformatted wholesale: a file that is already
# off-style may stay so, but no C# file changed since BASE may get MORE `dotnet format whitespace` findings than it had
# in BASE (a new file must have none). Covers the two shipped projects, HP1.Core and hpengine.
#
#   sh hpengine/tests/style_check.sh BASE        BASE: a commit (the PR base, or the previous push)
# Exit: 0 ok, 1 a file got worse (named), 2 usage error.
set -eu
[ $# -eq 1 ] || { echo "usage: style_check.sh BASE" >&2; exit 2; }
BASE=$1
cd "$(git rev-parse --show-toplevel)"
git rev-parse -q --verify "$BASE^{commit}" >/dev/null || { echo "style_check: not a commit: $BASE" >&2; exit 2; }
CHANGED=$(git diff --name-only --diff-filter=AMR "$BASE" -- 'HP1.Core/*.cs' 'hpengine/*.cs')
[ -n "$CHANGED" ] || { echo "style_check: no changed C# files"; exit 0; }

TMP=$(mktemp -d "${TMPDIR:-/tmp}/style_check.XXXXXX")
trap 'rm -rf "$TMP"' EXIT
TMP=$(cd "$TMP" && pwd -P)   # dotnet format prints resolved paths (macOS: /tmp -> /private/tmp)
# Findings per file: "<count> <path>". Both trees are judged by the CURRENT .editorconfig.
findings() {
    for p in HP1.Core/HP1.Core.csproj hpengine/HPEngine.csproj; do
        dotnet format whitespace "$1/$p" --verify-no-changes -v q 2>&1 || true
    done | sed -n "s|^$1/\([^(]*\.cs\)(.*error WHITESPACE.*|\1|p" | sort | uniq -c
}
mkdir "$TMP/base"
git archive "$BASE" HP1.Core hpengine | tar -x -C "$TMP/base"
cp .editorconfig "$TMP/base/.editorconfig"
findings "$(pwd -P)" > "$TMP/head.txt"
findings "$TMP/base" > "$TMP/base.txt"

rc=0
for f in $CHANGED; do
    h=$(awk -v f="$f" '$2 == f { print $1 }' "$TMP/head.txt"); h=${h:-0}
    b=$(awk -v f="$f" '$2 == f { print $1 }' "$TMP/base.txt"); b=${b:-0}
    if [ "$h" -gt "$b" ]; then
        echo "style_check: $f: $b -> $h whitespace findings (fix: dotnet format whitespace <project> --include $f)"
        rc=1
    fi
done
[ $rc -eq 0 ] && echo "style_check: $(echo "$CHANGED" | wc -l | tr -d ' ') changed C# file(s), none got worse"
exit $rc
