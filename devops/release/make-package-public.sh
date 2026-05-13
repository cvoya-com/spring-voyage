#!/usr/bin/env bash
# devops/release/make-package-public.sh — set a GHCR container package to
# public visibility, retrying on transient failure to ride out GHCR's
# first-publish propagation race.
#
# Background: GHCR's `/orgs/<org>/packages/container/<name>` admin endpoint
# is not immediately queryable after a brand-new package's first push.
# There is a propagation delay (observed ~10–30 seconds) during which the
# PATCH returns HTTP 404. Existing packages don't hit this. Without retry,
# the release workflow's "Make GHCR package public" step fails on the
# first release that creates a new package, leaving the GitHub Release in
# its draft state and requiring an operator-triggered rerun.
#
# Used by `.github/workflows/release.yml`'s four "Make GHCR package
# public" steps. Org is hardcoded as `cvoya-com` — Spring Voyage is the
# only consumer.
#
# Usage:
#   devops/release/make-package-public.sh <package-name>
#
# Requires `gh` on PATH and a token in `GH_TOKEN` (the workflow's
# GITHUB_TOKEN suffices once the package is linked to the source repo via
# the org's "Inherit access from source repository" setting).
#
# Exits 0 if the PATCH succeeds within 5 attempts (~150s total backoff),
# 1 otherwise with an operator-facing manual-fix hint.

set -euo pipefail

PKG="${1:?usage: $0 <package-name>}"
ORG="cvoya-com"

for attempt in 1 2 3 4 5; do
  if gh api -X PATCH "/orgs/${ORG}/packages/container/${PKG}" \
       -F visibility=public 2>&1; then
    echo "Set ${PKG} to public on attempt ${attempt}."
    exit 0
  fi
  delay=$((attempt * 10))
  echo "::warning::PATCH on ${PKG} failed (attempt ${attempt}/5); retrying in ${delay}s."
  sleep "$delay"
done

echo "::error::Failed to set ${PKG} to public after 5 attempts (~150s total wait)."
echo "::error::GHCR's first-publish propagation can occasionally take longer than 2.5 min."
echo "::error::Manual fix: https://github.com/orgs/${ORG}/packages/container/${PKG}/settings"
exit 1
