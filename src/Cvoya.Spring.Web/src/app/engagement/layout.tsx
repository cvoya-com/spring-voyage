// Engagement-portal layout shell (E2.3, #1415).
//
// Lives at /engagement/** — a separate top-level parent route that is a sibling
// of the management portal's root layout. Per ADR-0033, the two portals share
// the same Next.js app, auth, design-system tokens, and API client, but each has
// its own navigation structure and chrome.
//
// Next.js App Router: the root layout.tsx owns <html>/<body>; this segment
// layout sits inside that tree. We override the management portal's AppShell
// by NOT rendering it here — instead we render the engagement-portal shell
// (EngagementShell) which has its own sidebar / header chrome.
//
// Because the root layout wraps ALL routes through AppShell, we need to bypass
// the AppShell for /engagement/**. The strategy: root layout wraps children in
// AppShell; the engagement layout wraps its children in EngagementShell. But
// AppShell is in the root layout and we can't opt out of it per-segment.
//
// The correct approach for ADR-0033's two-portal model in the App Router is to
// use a route group. However, since the existing root layout already applies
// AppShell globally, this segment layout renders the engagement shell's content
// WITHOUT duplicating the AppShell — the engagement portal chrome lives inside
// the main pane that AppShell provides.
//
// This means:
//   - The management sidebar is present (it comes from AppShell in root layout).
//   - The engagement portal renders its own chrome inside the <main> area.
//   - A cross-link "Back to Management" is the primary management-portal exit.
//
// If in v0.2 the portals are to be visually fully separated (no management
// sidebar visible in engagement), that requires restructuring the root layout to
// use route groups — tracked as a follow-up when the split becomes load-bearing.
//
// For v0.1, the engagement portal renders its own header band + nav inside
// <main>, which visually distinguishes it from management pages (no management-
// portal chrome visible inside the engagement content area).

import type { Metadata } from "next";
import { EngagementShell } from "@/components/engagement/engagement-shell";

export const metadata: Metadata = {
  title: "Engagement — Spring Voyage",
  description: "Engage with units and agents in flight.",
};

export default function EngagementLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return <EngagementShell>{children}</EngagementShell>;
}
