// Analytics index — redirects to the Costs tab, which is the only
// subsection implemented in v1. Throughput and Wait times are tracked by
// #448 / `docs/design/portal-exploration.md` § 5.7 and ship behind their
// own placeholder routes so the sidebar and breadcrumbs can name them
// without pointing at a 404.
import { redirect } from "next/navigation";

export default function AnalyticsIndexRedirect(): never {
  redirect("/analytics/costs");
}
