// Central slug -> React component registry. Extension seam: when a new
// connector lands, add a new entry here pointing at the component(s) under
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
// Each connector can ship up to two entry points:
//
//   * `connector-tab.tsx` (required) — rendered inside the Connector tab
//     of an already-bound unit. Operates against a live unit id.
//   * `connector-wizard-step.tsx` (optional, #199) — rendered inside
//     Step 3 of the create-unit wizard. Runs *before* the unit exists,
//     so it has no unit id and bubbles config up to the wizard, which
//     bundles it into the single transactional create-unit call.
//
// Consistency between the .NET connector slug, the registry entry, and
// the web submodule on disk is enforced in CI by
// `eng/ci/validate-connector-web.sh`.

import type { ComponentType } from "react";

import { GitHubConnectorTab } from "@connector-github/connector-tab";
import { GitHubConnectorWizardStep } from "@connector-github/connector-wizard-step";
import { WebSearchConnectorTab } from "@connector-web-search/connector-tab";
import { WebSearchConnectorWizardStep } from "@connector-web-search/connector-wizard-step";

import type {
  UnitGitHubConfigRequest,
  UnitWebSearchConfigRequest,
} from "@/lib/api/types";

export interface ConnectorTabProps {
  unitId: string;
}

/**
 * Props for a connector's wizard-step component. The wizard passes an
 * `onChange` callback; the component fires it whenever the form produces
 * a valid connector config payload (or `null` to indicate "not ready").
 *
 * The payload shape is intentionally open (`Record<string, unknown>`) so
 * the wizard treats it opaquely — each connector's server-side
 * `IConnectorType` validates the concrete shape when the bundled
 * create-unit request arrives.
 */
export interface ConnectorWizardStepProps {
  onChange: (body: Record<string, unknown> | null) => void;
  initialValue?: Record<string, unknown> | null;
}

interface ConnectorRegistryEntry {
  /** The slug must match the server-side IConnectorType.Slug. */
  slug: string;
  /**
   * Component rendered on the unit's Connector tab after binding. Each
   * connector owns its own form — the registry only decides which
   * component to mount.
   */
  tab: ComponentType<ConnectorTabProps>;
  /**
   * Component rendered inside the create-unit wizard (Step 3). Optional —
   * connectors can choose to only expose themselves post-creation. When
   * absent, the wizard renders a "configure after creation" hint.
   */
  wizardStep?: ComponentType<ConnectorWizardStepProps>;
}

// The GitHub wizard step is typed against UnitGitHubConfigRequest; the
// registry stores it as the generic `ConnectorWizardStepProps` contract.
// The cast is safe because:
//   1. The server's connector binding endpoint treats the config payload
//      as opaque JSON until it reaches the GitHub connector's route.
//   2. `UnitGitHubConfigRequest` is structurally a `Record<string, ...>`.
// The alternative (making the entry itself generic) would force every
// caller to name the connector-specific type, which defeats the purpose
// of a polymorphic registry.
const githubWizardStep =
  GitHubConnectorWizardStep as unknown as ComponentType<ConnectorWizardStepProps>;
const webSearchWizardStep =
  WebSearchConnectorWizardStep as unknown as ComponentType<ConnectorWizardStepProps>;

const ENTRIES: ReadonlyArray<ConnectorRegistryEntry> = [
  {
    slug: "github",
    tab: GitHubConnectorTab,
    wizardStep: githubWizardStep,
  },
  {
    slug: "web-search",
    tab: WebSearchConnectorTab,
    wizardStep: webSearchWizardStep,
  },
];

/**
 * Returns the React component registered for the given connector slug on
 * the Connector tab surface, or `undefined` when no UI is available
 * (happens for connector types the web project wasn't built against —
 * the Connector tab falls back to a generic "no UI available" state).
 */
export function getConnectorComponent(
  slug: string,
): ComponentType<ConnectorTabProps> | undefined {
  return ENTRIES.find((e) => e.slug === slug)?.tab;
}

/**
 * Returns the wizard-step component registered for the given slug, or
 * `undefined` when the connector doesn't ship a wizard UI. The wizard
 * should render a fallback hint in that case.
 */
export function getConnectorWizardStep(
  slug: string,
): ComponentType<ConnectorWizardStepProps> | undefined {
  return ENTRIES.find((e) => e.slug === slug)?.wizardStep;
}

/** Returns every registered slug. Useful for dev tooling / diagnostics. */
export function getRegisteredConnectorSlugs(): string[] {
  return ENTRIES.map((e) => e.slug);
}

// Re-exports kept so the wizard doesn't need to pull the connector-
// specific types directly.
export type { UnitGitHubConfigRequest, UnitWebSearchConfigRequest };
