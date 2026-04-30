#!/usr/bin/env bash
# scripts/extract-changelog-section.sh — extract a named section from CHANGELOG.md
#
# Prints the body of the first matching section header to stdout so the
# release.yml workflow can feed it directly into gh release create --notes.
#
# Usage:
#   scripts/extract-changelog-section.sh [SECTION]
#
# SECTION defaults to "Unreleased". To extract a specific version:
#   scripts/extract-changelog-section.sh "0.1.0"
#
# The script matches the first `## [SECTION]` header (case-insensitive for
# "Unreleased"; exact otherwise) and emits every line until the next `## [`
# header, stripping leading/trailing blank lines.
#
# Exit codes:
#   0 — section found and printed
#   1 — section not found (error message on stderr)

set -euo pipefail

SECTION="${1:-Unreleased}"

CHANGELOG="${BASH_SOURCE[0]%/*}/../CHANGELOG.md"
if [[ ! -f "$CHANGELOG" ]]; then
    echo "::error::CHANGELOG.md not found at $CHANGELOG" >&2
    exit 1
fi

# Extract lines between the matched `## [SECTION]` header (exclusive) and the
# next `## [` header (exclusive). Uses grep line numbers rather than awk -v
# pattern injection to avoid backslash-escaping issues with bracket chars in
# regex passed through shell variable expansion.
#
# Algorithm:
#   1. Find the line number of the target section header.
#   2. Find the line number of the next section header after it (or EOF).
#   3. Extract the lines between them and strip leading/trailing blank lines.

# Step 1: locate the target header.
if [[ "${SECTION,,}" == "unreleased" ]]; then
    target_line=$(grep -n '^\#\# \[Unreleased\]' "$CHANGELOG" | head -n1 | cut -d: -f1)
else
    # Escape dots for grep basic regex.
    escaped="${SECTION//./\\.}"
    target_line=$(grep -n "^## \[${escaped}\]" "$CHANGELOG" | head -n1 | cut -d: -f1)
fi

if [[ -z "$target_line" ]]; then
    echo "::error::Section '## [${SECTION}]' not found in CHANGELOG.md" >&2
    exit 1
fi

# Step 2: find next ## [ header after the target.
total_lines=$(wc -l < "$CHANGELOG")
next_line=$(awk -v start="$target_line" 'NR > start && /^## \[/ { print NR; exit }' "$CHANGELOG")
if [[ -z "$next_line" ]]; then
    next_line=$((total_lines + 1))
fi

# Step 3: extract body lines (between headers, exclusive on both ends).
body_start=$((target_line + 1))
body_end=$((next_line - 1))

if [[ "$body_start" -gt "$body_end" ]]; then
    # Empty section — emit nothing (not an error).
    exit 0
fi

# Extract the slice, then strip leading and trailing blank lines.
awk -v s="$body_start" -v e="$body_end" '
    NR >= s && NR <= e { lines[++n] = $0 }
    END {
        # Skip leading blank lines.
        start = 1
        while (start <= n && lines[start] == "") { start++ }
        # Skip trailing blank lines.
        finish = n
        while (finish >= start && lines[finish] == "") { finish-- }
        for (i = start; i <= finish; i++) { print lines[i] }
    }
' "$CHANGELOG"
