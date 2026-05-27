#!/usr/bin/env bash
# Validates the connector-web wiring across the monorepo.
#
# Each connector under src/Cvoya.Spring.Connector.<Name>/ may optionally
# ship a web/ submodule holding its React/TypeScript UI. A connector's
# binding scope (see IConnectorType.BindingScope in the .NET source)
# decides which surface the UI plugs into:
#
#   * Unit-scoped (BindingScope.Unit) — the connector binds per-unit, so
#     the UI renders inside the Connector tab of an already-bound unit
#     and (optionally) as Step 3 of the create-unit wizard. Entry files:
#       - `connector-tab.tsx`        (required) — exports
#         `<PascalCase>ConnectorTab`. Registered in
#         src/Cvoya.Spring.Web/src/connectors/registry.ts.
#       - `connector-wizard-step.tsx` (optional, #199) — exports
#         `<PascalCase>ConnectorWizardStep`. Registered in the same
#         registry alongside the tab entry.
#
#   * Tenant-scoped (BindingScope.Tenant, ADR-0061 §1) — the connector
#     binds once per tenant, so there's no per-unit surface to host its
#     UI. Instead the UI renders as a settings drawer panel reachable
#     from `/settings`. Entry file:
#       - `connector-panel.tsx` (required) — exports
#         `<PascalCase>ConnectorPanel`. Registered as a drawer panel in
#         src/Cvoya.Spring.Web/src/lib/extensions/defaults.tsx (imported
#         via the same @connector-<slug>/* tsconfig alias).
#
# A connector's web/ submodule must declare exactly one of these two
# shapes — they describe mutually-exclusive rendering contexts.
#
# Invariants enforced:
#
#   1. Every connector package with a web/ subdirectory ships exactly
#      one entry file (`connector-tab.tsx` xor `connector-panel.tsx`)
#      and a package.json workspace manifest (so the npm workspace root
#      can hoist its deps).
#   2. The entry file exports a component named `<PascalCase>ConnectorTab`
#      (unit-scoped) or `<PascalCase>ConnectorPanel` (tenant-scoped) —
#      drift guard between the .NET package name and the JS identifier
#      the consumer imports.
#   3. Unit-scoped: if a wizard-step file (`connector-wizard-step.tsx`)
#      is present it must export `<PascalCase>ConnectorWizardStep`.
#      Tenant-scoped: a wizard-step file is rejected (the create-unit
#      wizard is unit-scoped by definition).
#   4. Unit-scoped: every slug ships a registry entry in registry.ts,
#      and every registry slug has a matching on-disk submodule.
#   5. Tenant-scoped: defaults.tsx imports the panel via the
#      `@connector-<slug>/connector-panel` alias, ensuring the drawer
#      panel registry actually mounts the connector's UI.
#
# Patterned after the "Validate agent definition references" step in
# .github/workflows/ci.yml — simple pure-bash checks, zero runtime
# dependencies, deliberately strict on layout.
#
# Exit 0 => all connectors validated (or none exist yet).
# Exit 1 => at least one invariant violated; messages are formatted as
#   GitHub workflow annotations (::error::) so CI surfaces them inline.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

CONNECTOR_GLOB="src/Cvoya.Spring.Connector.*"
REGISTRY_FILE="src/Cvoya.Spring.Web/src/connectors/registry.ts"
DEFAULTS_FILE="src/Cvoya.Spring.Web/src/lib/extensions/defaults.tsx"

failed=0

if [ ! -f "$REGISTRY_FILE" ]; then
  echo "::error file=$REGISTRY_FILE::Connector registry not found; expected at $REGISTRY_FILE"
  exit 1
fi

if [ ! -f "$DEFAULTS_FILE" ]; then
  echo "::error file=$DEFAULTS_FILE::Drawer-panel defaults file not found; expected at $DEFAULTS_FILE"
  exit 1
fi

# ---------------------------------------------------------------------
# Collect the set of slugs mentioned in the registry. The registry
# declares entries like:
#     { slug: "github", tab: GitHubConnectorTab },
# We grep the literal `slug: "<value>"` occurrences. Used to enforce
# unit-scoped invariants 4 and 5.
# ---------------------------------------------------------------------
# `grep -o` returns 1 when no matches — tolerated here so an empty
# registry (zero unit-scoped connectors) is a valid state. `set -o pipefail`
# would otherwise fail the assignment.
registry_slugs=$(grep -oE 'slug:[[:space:]]*"[^"]+"' "$REGISTRY_FILE" \
  | sed -E 's/slug:[[:space:]]*"([^"]+)"/\1/' \
  | sort -u || true)

# Track which on-disk slugs are unit-scoped so we can reconcile against
# `registry_slugs` after the per-package loop. Tenant-scoped slugs are
# *not* registered in registry.ts so they don't participate in this
# reconciliation.
seen_unit_slugs_on_disk=""

for pkg_dir in $CONNECTOR_GLOB; do
  [ -d "$pkg_dir" ] || continue

  pkg_name="${pkg_dir#src/Cvoya.Spring.Connector.}"
  # Derive the slug from the package name. Default convention is
  # lowercased package name (`GitHub` → `github`, `Arxiv` → `arxiv`).
  # When the server-side `IConnectorType.Slug` deliberately differs
  # from that default — e.g. a kebab-cased slug — add an explicit
  # mapping below. The mapping table is the single point of drift
  # between the .NET package layout and the web registry; keep it
  # small and obvious.
  case "$pkg_name" in
    WebSearch) slug="web-search" ;;
    *)         slug="$(echo "$pkg_name" | tr '[:upper:]' '[:lower:]')" ;;
  esac
  web_dir="$pkg_dir/web"

  if [ ! -d "$web_dir" ]; then
    # Connector package with no web UI — that's allowed (a headless
    # connector is still a valid connector). Move on.
    continue
  fi

  tab_file="$web_dir/connector-tab.tsx"
  panel_file="$web_dir/connector-panel.tsx"
  wizard_file="$web_dir/connector-wizard-step.tsx"
  pkg_json="$web_dir/package.json"

  has_tab=0
  has_panel=0
  [ -f "$tab_file" ] && has_tab=1
  [ -f "$panel_file" ] && has_panel=1

  if [ "$has_tab" -eq 0 ] && [ "$has_panel" -eq 0 ]; then
    echo "::error file=$web_dir::Connector '$slug' has a web/ submodule but is missing both 'connector-tab.tsx' (unit-scoped) and 'connector-panel.tsx' (tenant-scoped). Ship exactly one — the choice mirrors IConnectorType.BindingScope in the .NET source."
    failed=1
    continue
  fi

  if [ "$has_tab" -eq 1 ] && [ "$has_panel" -eq 1 ]; then
    echo "::error file=$web_dir::Connector '$slug' ships both 'connector-tab.tsx' and 'connector-panel.tsx'. These describe mutually-exclusive rendering contexts (per-unit Connector tab vs tenant Settings drawer); pick one based on the connector's BindingScope."
    failed=1
    continue
  fi

  if [ ! -f "$pkg_json" ]; then
    echo "::error file=$web_dir::Connector '$slug' web/ submodule is missing 'package.json' — required so the npm workspace root can hoist its peer dependencies (see src/Cvoya.Spring.Web/next.config.ts and root package.json)."
    failed=1
  fi

  if [ "$has_tab" -eq 1 ]; then
    # ---------------------------------------------------------------
    # Unit-scoped path.
    # ---------------------------------------------------------------
    seen_unit_slugs_on_disk="$seen_unit_slugs_on_disk
$slug"

    # Component identifier drift guard: the exported component must be
    # `<PascalPackageName>ConnectorTab`. Derived by uppercasing the
    # first character of the package name (GitHub -> GitHubConnectorTab).
    # The registry imports this identifier; if they drift the build
    # breaks, but we'd rather fail here with a pointed message than at
    # bundle time.
    expected_component="${pkg_name}ConnectorTab"
    if ! grep -qE "export (function|const) ${expected_component}\b" "$tab_file"; then
      echo "::error file=$tab_file::Expected an export named '${expected_component}' (derived from the connector package name '${pkg_name}'). The web registry imports it by that name — rename the component or align the package name."
      failed=1
    fi

    # Optional wizard-step entry point (#199). A connector that ships a
    # wizard-step UI must export `<PascalPackageName>ConnectorWizardStep`
    # so the registry can statically import it. Absence of the file is
    # fine — wizard Step 3 falls back to a "configure after creation"
    # hint for that connector.
    if [ -f "$wizard_file" ]; then
      expected_wizard="${pkg_name}ConnectorWizardStep"
      if ! grep -qE "export (function|const) ${expected_wizard}\b" "$wizard_file"; then
        echo "::error file=$wizard_file::Expected an export named '${expected_wizard}' (derived from the connector package name '${pkg_name}'). The web registry imports it by that name for the create-unit wizard — rename the component or align the package name."
        failed=1
      fi
    fi

    # The registry must have a matching entry for this on-disk slug.
    if ! echo "$registry_slugs" | grep -qx "$slug"; then
      echo "::error file=$REGISTRY_FILE::Connector '$slug' ships a web/ submodule at '$web_dir' but has no entry in the web registry. Add { slug: \"$slug\", tab: ${pkg_name}ConnectorTab } and import it via the @connector-${slug}/* path alias."
      failed=1
    fi
  else
    # ---------------------------------------------------------------
    # Tenant-scoped path.
    # ---------------------------------------------------------------
    # A wizard-step file makes no sense alongside a panel — the wizard
    # is unit-scoped by definition (no unit, no Step 3).
    if [ -f "$wizard_file" ]; then
      echo "::error file=$wizard_file::Connector '$slug' ships 'connector-panel.tsx' (tenant-scoped) alongside 'connector-wizard-step.tsx'. The create-unit wizard is unit-scoped — delete the wizard-step file or convert the connector to unit-scope."
      failed=1
    fi

    # Component identifier drift guard for the panel entry.
    expected_component="${pkg_name}ConnectorPanel"
    if ! grep -qE "export (function|const) ${expected_component}\b" "$panel_file"; then
      echo "::error file=$panel_file::Expected an export named '${expected_component}' (derived from the connector package name '${pkg_name}'). $DEFAULTS_FILE imports it by that name — rename the component or align the package name."
      failed=1
    fi

    # Drawer-panel registration: defaults.tsx must import the panel
    # via the `@connector-<slug>/connector-panel` alias. We don't try
    # to validate the drawer-panel object literal itself — a missing
    # import will surface immediately at typecheck time, but catching
    # it here keeps the failure messages localised to this script.
    if ! grep -qE "from[[:space:]]+\"@connector-${slug}/connector-panel\"" "$DEFAULTS_FILE"; then
      echo "::error file=$DEFAULTS_FILE::Tenant-scoped connector '$slug' ships 'connector-panel.tsx' but $DEFAULTS_FILE does not import '${expected_component}' from '@connector-${slug}/connector-panel'. Add the import and a drawer-panel entry."
      failed=1
    fi
  fi
done

# ---------------------------------------------------------------------
# Every slug referenced in the registry must have a matching on-disk
# connector package with a unit-scoped web/ submodule. Otherwise the
# registry imports a component the build cannot resolve. Tenant-scoped
# connectors are intentionally absent from registry.ts so they don't
# participate here.
# ---------------------------------------------------------------------
for slug in $registry_slugs; do
  if ! echo "$seen_unit_slugs_on_disk" | grep -qx "$slug"; then
    echo "::error file=$REGISTRY_FILE::Registry references connector slug '$slug' but no matching unit-scoped connector package was found. Expected a web submodule at src/Cvoya.Spring.Connector.<Name>/web/connector-tab.tsx where <Name> lower-cased equals '$slug'."
    failed=1
  fi
done

if [ "$failed" -ne 0 ]; then
  echo
  echo "Connector web validation failed: see the ::error:: annotations above."
  exit 1
fi

echo "All connector web submodules are consistent with their registration surfaces."
