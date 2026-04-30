// Engagement portal landing page (E2.3, #1415).
//
// URL: /engagement
//
// Design choice: redirect to /engagement/mine rather than duplicate the list
// view here. The canonical "entry point" for an engagement user is "my
// engagements" — the list of threads they are a participant in. A bare /engagement
// URL should behave identically; a redirect avoids maintaining two surfaces that
// would diverge when E2.4 fills in the list view.
//
// The redirect is a permanent (308) server redirect so link-preload and browser
// history both point at the canonical URL. If E2 later adds an "all engagements"
// view at /engagement (e.g. a tenant-wide overview), this file becomes that page
// and the redirect is removed — that change is scoped to E2.4's work.

import { redirect } from "next/navigation";

export default function EngagementPage() {
  redirect("/engagement/mine");
}
