// Central slug -> React component registry. Extension seam: when a new
// connector lands, add a new entry here pointing at the component under
// that connector package's `web/` subdirectory (see
// `src/Cvoya.Spring.Connector.GitHub/web/` for the canonical shape).
//
// Today's implementation is statically-imported: the registry knows each
// component at build time. Hot-loading / dynamic imports are deliberately
// out of scope until a second connector lands (see #195 for the runtime
// discovery follow-up).
//
// Validation of the web submodule wiring (consistency between the .NET
// connector type and the web component for the same slug, integrity of
// the tsconfig path alias, etc.) is tracked as #196.

import type { ComponentType } from "react";

import { GitHubConnectorTab } from "./github/connector-tab";

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
