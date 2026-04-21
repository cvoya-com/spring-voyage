"use client";

/**
 * /admin/connectors — read-only admin view (#691).
 *
 * The installed-connector + credential-health content now lives in the
 * shared `<ConnectorHealthPanel>` component so both this legacy admin
 * route and the new Health tab on `/connectors` render the same panel
 * (#868). The admin route stays in place until `DEL-admin-top` (#876)
 * retires it; until then it renders the shared panel under its own
 * page heading.
 *
 * Cross-reference: `docs/user-guide/connectors.md` covers the CLI
 * workflows the "Operator guide" link deep-links into.
 */

import { Plug } from "lucide-react";

import { ConnectorHealthPanel } from "@/components/connectors/health-panel";

export default function AdminConnectorsPage() {
  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <Plug className="h-5 w-5" aria-hidden="true" /> Connector health
        </h1>
        <p className="text-sm text-muted-foreground">
          Installed connectors on the current tenant and their
          credential-health status.
        </p>
      </div>

      <ConnectorHealthPanel />
    </div>
  );
}
