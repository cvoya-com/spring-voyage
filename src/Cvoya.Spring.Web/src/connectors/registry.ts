// Central slug -> React component registry. Extension seam: when a new
// connector lands, add a new entry here pointing at the component under
// that connector package's `web/` subdirectory (see
// `src/Cvoya.Spring.Connector.GitHub/web/` for the canonical shape).
//
// Each connector package owns its own web directory; the web project
// references it through a `@connector-<slug>/*` tsconfig path alias
// (see `tsconfig.json`) and Turbopack resolves cross-directory
// `node_modules` imports via `turbopack.root` in `next.config.ts`.
//
// Today's implementation is statically-imported: the registry knows each
// component at build time. Hot-loading / dynamic imports are deliberately
// out of scope until a second connector lands (see #195 for the runtime
// discovery follow-up).
//
// Consistency between the .NET connector slug, the registry entry, and
// the web submodule on disk is enforced in CI by
// `scripts/validate-connector-web.sh`.

import type { ComponentType } from "react";

import { GitHubConnectorTab } from "@connector-github/connector-tab";

export interface ConnectorTabProps {
  unitId: string;
}

interface ConnectorRegistryEntry {
  /** The slug must match the server-side IConnectorType.Slug. */
  slug: string;
  /**
   * React component rendered inside the Connector tab of the unit config
   * page. Each connector owns its own form — the registry only decides
   * which component to mount.
   */
  component: ComponentType<ConnectorTabProps>;
}

const ENTRIES: ReadonlyArray<ConnectorRegistryEntry> = [
  { slug: "github", component: GitHubConnectorTab },
];

/**
 * Returns the React component registered for the given connector slug,
 * or `undefined` if no UI is available for it (happens for connector
 * types the web project wasn't built against — the Connector tab falls
 * back to a generic "no UI available" state).
 */
export function getConnectorComponent(
  slug: string,
): ComponentType<ConnectorTabProps> | undefined {
  return ENTRIES.find((e) => e.slug === slug)?.component;
}

/** Returns every registered slug. Useful for dev tooling / diagnostics. */
export function getRegisteredConnectorSlugs(): string[] {
  return ENTRIES.map((e) => e.slug);
}
