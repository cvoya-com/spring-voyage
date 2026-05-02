/**
 * /installs/[id] — install-status view (ADR-0035 decision 11 / #1565).
 *
 * Renders the three terminal states for a package install:
 *   - staging — Phase 2 in progress; polls GET /api/v1/installs/{id} every 2 s.
 *   - active  — Phase 2 complete; all packages active. Stops polling.
 *   - failed  — One or more packages failed Phase 2. Shows retry + abort actions.
 *
 * This is a server-component wrapper that awaits params and delegates to
 * the client component below via dynamic import (to keep the polling logic
 * out of the server bundle).
 */

import InstallStatusClient from "./install-status-client";

interface PageProps {
  params: Promise<{ id: string }>;
}

export default async function InstallStatusPage({ params }: PageProps) {
  const { id } = await params;
  return <InstallStatusClient id={id} />;
}
