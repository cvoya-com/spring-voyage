"use client";

// /settings/user-identity — user-identity page (ADR-0047 §§ 2, 4, 14).
//
// Display-only surface. One card per registered connector, schema-
// driven (each connector contributes a JSON schema describing the
// per-tenant-user display fields it owns; for GitHub that's
// `{ username, display_handle? }`). No PAT input on this page —
// outbound credentials live on unit bindings (ADR-0047 §11), acquired
// via the new-unit wizard's auth-choice sub-step or the CLI's
// `spring user identity authorize-github` verb.
//
// The orphan-secrets panel surfaces tenant secrets the OAuth flow
// wrote under the binding-scoped naming convention so operators can
// clean up unused entries that never made it onto a binding.
//
// In OSS the page's `tenantUserId` is the well-known operator id
// (`OssTenantUserIds.Operator`) pinned by ADR-0047 §3. The cloud
// overlay reads the calling tenant user's id from the hosted auth
// context; v0.1's freezing release ships with the OSS-only constant.

import { IdCard } from "lucide-react";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { ConnectorIdentityCard } from "@/components/user-identity/connector-card";
import { OrphanSecretsPanel } from "@/components/user-identity/orphan-secrets";
import { YourHatsPanel } from "@/components/user-identity/your-hats-panel";
import {
  useConnectorTypes,
  useTenantUser,
  useTenantUserIdentities,
} from "@/lib/api/queries";
import { formatTranslatedError } from "@/lib/api/translate-error";

/**
 * OSS operator's tenant-user id (ADR-0047 §3). Mirror of
 * `Cvoya.Spring.Core.Tenancy.OssTenantUserIds.OperatorDashed`. Pinned
 * here so the dashed form is a `const string` at build time — the
 * portal does not call into the .NET layer to compute it. Reproducible
 * from outside the platform via
 * `uuid.uuid5(uuid.UUID("00000000-0000-0000-0000-000000000000"),
 *  "cvoya/tenant-user/oss-operator")`.
 */
export const OSS_OPERATOR_TENANT_USER_ID =
  "5c4c8e29-d91b-5b50-8651-64536cfb68ee";

export default function UserIdentityPage() {
  const tenantUserId = OSS_OPERATOR_TENANT_USER_ID;
  const userQuery = useTenantUser(tenantUserId);
  const identitiesQuery = useTenantUserIdentities(tenantUserId);
  const connectorsQuery = useConnectorTypes();

  const loading =
    userQuery.isLoading ||
    identitiesQuery.isLoading ||
    connectorsQuery.isLoading;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <IdCard className="h-5 w-5" aria-hidden="true" /> User identity
        </h1>
        <p className="text-sm text-muted-foreground">
          How Spring Voyage renders you in connector-side surfaces —
          your GitHub login for <code>@</code>-mentions and reviewer
          invocations, your Slack handle for direct-message rendering,
          and so on. This page is strictly display identity (ADR-0047
          §4); outbound credentials live on unit bindings.
        </p>
      </div>

      {userQuery.isError && (
        <Card>
          <CardContent className="px-6 py-4 text-sm text-destructive">
            Could not load your tenant-user profile:{" "}
            {formatTranslatedError(userQuery.error)}
          </CardContent>
        </Card>
      )}

      {!loading && userQuery.data !== null && userQuery.data !== undefined && (
        <Card data-testid="user-identity-profile">
          <CardHeader className="gap-1">
            <CardTitle className="text-base">
              {userQuery.data.displayName}
            </CardTitle>
            <p className="text-xs text-muted-foreground">
              Tenant user{" "}
              <code className="rounded bg-muted px-1 py-0.5 text-[11px]">
                {userQuery.data.id}
              </code>
            </p>
          </CardHeader>
        </Card>
      )}

      <section
        aria-labelledby="user-identity-hats-heading"
        className="space-y-3"
      >
        <h2
          id="user-identity-hats-heading"
          className="text-sm font-medium uppercase tracking-wide text-muted-foreground"
        >
          Your Hats
        </h2>
        <p className="text-xs text-muted-foreground">
          The Humans bound to your account (ADR-0062). Each Hat is a
          role-slot you can receive messages on and reply as. Claim
          unbound Hats from a unit&apos;s Members tab.
        </p>
        <YourHatsPanel />
      </section>

      <section
        aria-labelledby="user-identity-connectors-heading"
        className="space-y-3"
      >
        <h2
          id="user-identity-connectors-heading"
          className="text-sm font-medium uppercase tracking-wide text-muted-foreground"
        >
          Connector identities
        </h2>
        {loading && (
          <div className="space-y-3">
            <Skeleton className="h-32" />
            <Skeleton className="h-32" />
          </div>
        )}
        {!loading && connectorsQuery.data && connectorsQuery.data.length === 0 && (
          <Card>
            <CardContent className="px-6 py-4 text-sm text-muted-foreground">
              No connectors are installed on this tenant.
            </CardContent>
          </Card>
        )}
        {!loading && connectorsQuery.data && (
          <div
            className="grid grid-cols-1 gap-3 md:grid-cols-2"
            data-testid="user-identity-connector-grid"
          >
            {connectorsQuery.data.map((c) => {
              const identity =
                identitiesQuery.data?.find(
                  (i) => i.connectorId === c.typeSlug,
                ) ?? null;
              return (
                <ConnectorIdentityCard
                  key={c.typeId}
                  tenantUserId={tenantUserId}
                  connectorSlug={c.typeSlug}
                  connectorDisplayName={c.displayName}
                  identity={identity}
                />
              );
            })}
          </div>
        )}
      </section>

      <section
        aria-labelledby="user-identity-orphan-heading"
        className="space-y-3"
      >
        <h2
          id="user-identity-orphan-heading"
          className="text-sm font-medium uppercase tracking-wide text-muted-foreground"
        >
          Secret hygiene
        </h2>
        <OrphanSecretsPanel />
      </section>
    </div>
  );
}
