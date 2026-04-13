#!/usr/bin/env bash
# Validates the connector-web wiring across the monorepo.
#
# Each connector under src/Cvoya.Spring.Connector.<Name>/ may optionally
# ship a web/ submodule holding its React/TypeScript UI (consumed by
# src/Cvoya.Spring.Web via the @connector-<slug>/* tsconfig alias and
# registered in src/Cvoya.Spring.Web/src/connectors/registry.ts). This
# script enforces four invariants in CI:
#
#   1. Every connector package that has a web/ subdirectory declares the
#      expected entry file (`connector-tab.tsx`) and a package.json
#      workspace manifest (so the npm workspace root can hoist its deps).
#   2. The entry file exports a React component named `<PascalCase>ConnectorTab`
#      derived from the package name (drift guard between the .NET slug
#      and the component identifier).
#   3. Every connector slug referenced from the Web registry has a
#      matching on-disk submodule under the expected connector package.
#   4. Every connector package with a web/ subdirectory has a registry
#      entry (no orphaned submodules that ship code the web app cannot
#      discover).
#
# Patterned after the "Validate agent definition references" step in
# .github/workflows/ci.yml — simple pure-bash checks, zero runtime
# dependencies, deliberately strict on layout.
#
# Exit 0 => all connectors validated (or none exist yet).
# Exit 1 => at least one invariant violated; messages are formatted as
#   GitHub workflow annotations (::error::) so CI surfaces them inline.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

CONNECTOR_GLOB="src/Cvoya.Spring.Connector.*"
REGISTRY_FILE="src/Cvoya.Spring.Web/src/connectors/registry.ts"

failed=0

if [ ! -f "$REGISTRY_FILE" ]; then
  echo "::error file=$REGISTRY_FILE::Connector registry not found; expected at $REGISTRY_FILE"
  exit 1
fi

# ---------------------------------------------------------------------
# Collect the set of slugs mentioned in the registry. The registry
# declares entries like:
#     { slug: "github", component: GitHubConnectorTab },
# We grep the literal `slug: "<value>"` occurrences.
# ---------------------------------------------------------------------
# `grep -o` returns 1 when no matches — tolerated here so an empty
# registry (zero connectors) is a valid state. `set -o pipefail` would
# otherwise fail the assignment.
registry_slugs=$(grep -oE 'slug:[[:space:]]*"[^"]+"' "$REGISTRY_FILE" \
  | sed -E 's/slug:[[:space:]]*"([^"]+)"/\1/' \
  | sort -u || true)

if [ -z "$registry_slugs" ]; then
  echo "Connector registry has no entries — skipping submodule validation."
  echo "(Add an entry to $REGISTRY_FILE when a connector package ships a web/ submodule.)"
fi

# ---------------------------------------------------------------------
# For every connector package on disk, check the shape of its web/
# submodule (when present) and mark whether the registry covers it.
# The "seen" set is a plain newline-separated string so this script
# stays portable to the bash 3.x that ships on macOS CI runners (no
# associative arrays).
# ---------------------------------------------------------------------
seen_slugs_on_disk=""

for pkg_dir in $CONNECTOR_GLOB; do
  [ -d "$pkg_dir" ] || continue

  pkg_name="${pkg_dir#src/Cvoya.Spring.Connector.}"
  # Derive the slug from the package name — lowercase the first letter.
  # This matches the GitHub connector (package: `GitHub`, slug: `github`)
  # and every connector landing in the future is expected to follow the
  # same convention; if it doesn't, add the mapping here and document it.
  slug="$(echo "$pkg_name" | tr '[:upper:]' '[:lower:]')"
  web_dir="$pkg_dir/web"

  if [ ! -d "$web_dir" ]; then
    # Connector package with no web UI — that's allowed (a headless
    # connector is still a valid connector). Move on.
    continue
  fi

  seen_slugs_on_disk="$seen_slugs_on_disk
$slug"

  entry_file="$web_dir/connector-tab.tsx"
  pkg_json="$web_dir/package.json"

  if [ ! -f "$entry_file" ]; then
    echo "::error file=$web_dir::Connector '$slug' has a web/ submodule but is missing the expected entry file 'connector-tab.tsx'."
    failed=1
  fi

  if [ ! -f "$pkg_json" ]; then
    echo "::error file=$web_dir::Connector '$slug' web/ submodule is missing 'package.json' — required so the npm workspace root can hoist its peer dependencies (see src/Cvoya.Spring.Web/next.config.ts and root package.json)."
    failed=1
  fi

  # Component identifier drift guard: the exported component must be
  # `<PascalPackageName>ConnectorTab`. Derived by uppercasing the first
  # character of the package name (GitHub -> GitHubConnectorTab). The
  # registry imports this identifier; if they drift the build breaks,
  # but we'd rather fail here with a pointed message than at bundle time.
  if [ -f "$entry_file" ]; then
    expected_component="${pkg_name}ConnectorTab"
    if ! grep -qE "export (function|const) ${expected_component}\b" "$entry_file"; then
      echo "::error file=$entry_file::Expected an export named '${expected_component}' (derived from the connector package name '${pkg_name}'). The web registry imports it by that name — rename the component or align the package name."
      failed=1
    fi
  fi

  # The registry must have a matching entry for this on-disk slug.
  if ! echo "$registry_slugs" | grep -qx "$slug"; then
    echo "::error file=$REGISTRY_FILE::Connector '$slug' ships a web/ submodule at '$web_dir' but has no entry in the web registry. Add { slug: \"$slug\", component: ${pkg_name}ConnectorTab } and import it via the @connector-${slug}/* path alias."
    failed=1
  fi
done

# ---------------------------------------------------------------------
# Every slug referenced in the registry must have a matching on-disk
# connector package with a web/ submodule. Otherwise the registry
# imports a component the build cannot resolve.
# ---------------------------------------------------------------------
for slug in $registry_slugs; do
  if ! echo "$seen_slugs_on_disk" | grep -qx "$slug"; then
    echo "::error file=$REGISTRY_FILE::Registry references connector slug '$slug' but no matching connector package was found. Expected a web submodule at src/Cvoya.Spring.Connector.<Name>/web/ where <Name> lower-cased equals '$slug'."
    failed=1
  fi
done

if [ "$failed" -ne 0 ]; then
  echo
  echo "Connector web validation failed: see the ::error:: annotations above."
  exit 1
fi

echo "All connector web submodules are consistent with the registry."
