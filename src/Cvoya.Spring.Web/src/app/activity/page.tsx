// Activity index redirect — the surface split into Events / Interactions
// tabs in #2867, so `/activity` is no longer a leaf route. Existing
// bookmarks and the sidebar entry pointing at `/activity` land on the
// Events tab (the original event-stream content).
import { redirect } from "next/navigation";

export default function ActivityIndexRedirect(): never {
  redirect("/activity/events");
}
