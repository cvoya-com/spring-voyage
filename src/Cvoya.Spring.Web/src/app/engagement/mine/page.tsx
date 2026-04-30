// My engagements list — placeholder (E2.3, #1415).
//
// URL: /engagement/mine
//
// Empty placeholder for the engagement list view (E2.4, #1416 will fill).
//
// Cross-link URL shape:
//   From the management portal, a unit-detail or agent-detail page links here
//   with an optional filter query parameter:
//     /engagement/mine?unit=<unitId>    — engagements for a specific unit
//     /engagement/mine?agent=<agentId>  — engagements for a specific agent
//
// E2.4 reads these query parameters to pre-filter the list. This file is the
// canonical landing target for all management → engagement cross-links.

import { MessagesSquare } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";

export default function MyEngagementsPage() {
  return (
    <div className="space-y-6" data-testid="my-engagements-page">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <MessagesSquare className="h-5 w-5" aria-hidden="true" />
          My engagements
        </h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Threads you are a participant in, sorted by latest activity.
        </p>
      </div>

      <Card data-testid="my-engagements-empty-state">
        <CardContent className="flex flex-col items-center justify-center p-8 text-center">
          <MessagesSquare
            className="mb-3 h-10 w-10 text-muted-foreground"
            aria-hidden="true"
          />
          <p className="mb-1 font-medium">No engagements yet</p>
          <p className="text-sm text-muted-foreground">
            Start a unit and assign it a task to begin an engagement.
          </p>
        </CardContent>
      </Card>
    </div>
  );
}
