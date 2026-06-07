import { test } from "../../fixtures/test.js";

/**
 * Wizard: Catalog source branch — package WITH required inputs (#1615).
 *
 * SKIPPED — the feature this spec covered has been removed.
 *
 * 1. Package-level `inputs:` were removed from the manifest schema in
 *    ADR-0037 D2; connector-binding parameters moved to per-artefact
 *    `requires:`. The wizard's input-rendering path is dead code pending
 *    deletion (#1727): `CreateUnitPage`'s `selectedPackageInputs` is
 *    hard-coded to `[]`, so the `catalog-inputs` panel and the
 *    `catalog-input-<name>-control` / `-missing` fields this spec drove
 *    never render. `GET /packages/spring-voyage-oss` returns `inputs: null`.
 *
 * 2. The install-to-active assertion is also no longer reachable in this
 *    suite: `spring-voyage-oss` pins `runtime: claude-code` /
 *    `provider: anthropic` (declares a required `anthropic-oauth`
 *    credential) AND declares a required `github` connector. This suite
 *    is credential-free by design (dapr-agent + ollama, no operator
 *    secrets), so the install fails-fast at the credential/connector
 *    pre-flight. The credential-requirement surfacing on the catalog
 *    branch is covered credential-free by
 *    `04-units-create-from-template.spec.ts`; the full install-with-secret
 *    path lives in the CLI suite.
 */

test.describe("units — create from package with inputs (catalog wizard)", () => {
  test.skip("spring-voyage-oss package: typing inputs through the wizard reaches active", () => {
    // Intentionally empty — see the describe-block rationale. The wizard no
    // longer renders per-input fields (ADR-0037 D2 / #1727), and the
    // package can't install credential-free.
  });
});
