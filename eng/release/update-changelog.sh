#!/usr/bin/env bash
# eng/release/update-changelog.sh — regenerate the generated portion of CHANGELOG.md.
#
# CHANGELOG.md has two parts, split by a marker line:
#   • Above the marker — git-cliff-generated: the "# Changelog" header plus a
#     single rolled-up "## [Unreleased]" section built from the Conventional
#     Commits since BASELINE. Pre-release (alpha/beta/rc) tags are ignored as
#     version boundaries, so everything accumulates under [Unreleased] until a
#     stable cut, matching docs/developer/releases.md.
#   • The marker and everything below — hand-curated "Earlier history", frozen.
#
# This script regenerates ONLY the part above the marker; the frozen tail is
# preserved verbatim. release.sh runs it (as a gate) before a tag is cut, so the
# changelog can never drift more than one release. Run it by hand any time.
#
# Usage:
#   eng/release/update-changelog.sh           # regenerate in place
#   eng/release/update-changelog.sh --check   # exit 1 if regeneration would change the file (no write)
#
# Requires git-cliff (https://git-cliff.org).  Install:  brew install git-cliff
set -euo pipefail

HERE="${BASH_SOURCE[0]%/*}"
ROOT="$(cd "${HERE}/../.." && pwd)"
CONFIG="${ROOT}/cliff.changelog.toml"
CHANGELOG="${ROOT}/CHANGELOG.md"
MARKER="<!-- BEGIN FROZEN HISTORY -->"

# Boundary: the newest commit whose changes are captured in the frozen
# hand-curated history (PR #2969). Everything AFTER it is generated into
# [Unreleased]; #2970 and earlier live in the frozen tail. Changing this means
# re-doing the curated/generated split.
BASELINE="a7f12adac8895eca0b1644da8b2cdb44b98fcbbe"

# Pre-release tags are not version boundaries (see header comment).
IGNORE_TAGS='.*-(alpha|beta|rc)\.'

CHECK=false
[[ "${1:-}" == "--check" ]] && CHECK=true

if ! command -v git-cliff >/dev/null 2>&1; then
  echo "::error::git-cliff not found. Install it (brew install git-cliff) — see https://git-cliff.org." >&2
  exit 1
fi

if ! grep -qF "$MARKER" "$CHANGELOG"; then
  echo "::error::Frozen-history marker not found in CHANGELOG.md ('${MARKER}')." >&2
  echo "         CHANGELOG.md must keep the marker separating generated from curated history." >&2
  exit 1
fi

# Generated top: header + rolled-up [Unreleased] for BASELINE..HEAD.
generated="$(git-cliff --repository "$ROOT" --config "$CONFIG" --ignore-tags "$IGNORE_TAGS" "${BASELINE}..HEAD")"

# Frozen tail: the marker line and everything after it, preserved verbatim.
frozen="$(awk -v m="$MARKER" 'index($0, m){f=1} f' "$CHANGELOG")"

tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT
printf '%s\n\n%s\n' "$generated" "$frozen" > "$tmp"

if "$CHECK"; then
  if ! diff -q "$tmp" "$CHANGELOG" >/dev/null 2>&1; then
    echo "::error::CHANGELOG.md is out of date." >&2
    echo "         Run 'eng/release/update-changelog.sh' and commit the result." >&2
    exit 1
  fi
  echo "CHANGELOG.md is up to date."
  exit 0
fi

cp "$tmp" "$CHANGELOG"
echo "Regenerated CHANGELOG.md [Unreleased] from ${BASELINE:0:9}..HEAD."
