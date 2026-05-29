#!/usr/bin/env bash
# eng/release/resolve-release-notes.sh — resolve the GitHub Release body for a version.
#
# Release notes (the human-written narrative that becomes the GitHub Release
# body) are checked in under docs/releases/. This script picks the right file
# for the version being cut, with a fallback to the CHANGELOG so older tags and
# un-curated releases keep working exactly as before.
#
# Precedence:
#   1. docs/releases/<full-version>.md          exact, e.g. 1.0.0-rc.1.md
#   2. docs/releases/<major.minor.patch>.md     release line, e.g. 1.0.0.md
#   3. CHANGELOG.md [Unreleased]                 fallback (prior behaviour)
#
# (2) is the common case: one curated file per release line is reused by every
# pre-release (alpha/beta/rc) and the final stable cut of that version. Add an
# exact-version file only when a specific cut needs different notes.
#
# Prints the chosen notes body to stdout; logs the chosen source to stderr as a
# GitHub Actions ::notice:: so the release run records which file was used.
#
# Usage:
#   eng/release/resolve-release-notes.sh <version>
#
# Exit codes:
#   0 — notes resolved and printed (may be empty if a curated file is empty)
#   1 — usage error, or fallback could not find CHANGELOG [Unreleased]

set -euo pipefail

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
    echo "::error::usage: resolve-release-notes.sh <version>" >&2
    exit 1
fi

HERE="${BASH_SOURCE[0]%/*}"
RELEASES_DIR="${HERE}/../../docs/releases"

# Strip the pre-release/build suffix: 1.0.0-alpha.20260529.2 -> 1.0.0
BASE="${VERSION%%-*}"

exact_file="${RELEASES_DIR}/${VERSION}.md"
line_file="${RELEASES_DIR}/${BASE}.md"

if [[ -f "$exact_file" ]]; then
    echo "::notice::release body sourced from docs/releases/${VERSION}.md" >&2
    cat "$exact_file"
elif [[ -f "$line_file" ]]; then
    echo "::notice::release body sourced from docs/releases/${BASE}.md" >&2
    cat "$line_file"
else
    echo "::notice::no docs/releases file for ${VERSION} or ${BASE}; falling back to CHANGELOG [Unreleased]" >&2
    bash "${HERE}/extract-changelog-section.sh" "Unreleased"
fi
