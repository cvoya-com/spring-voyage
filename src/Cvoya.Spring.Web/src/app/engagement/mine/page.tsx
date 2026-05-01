// /engagement/mine
//
// The shell sidebar now hosts the live engagement list, so this route
// renders only the empty selection state — a "select an engagement to
// open the conversation" prompt. Cross-portal deep-links of the form
// /engagement/mine?unit=<id> and /engagement/mine?agent=<id> still work:
// the shell reads those params to scope the sidebar list accordingly.

import { MessagesSquare } from "lucide-react";
import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "My engagements — Spring Voyage",
};

interface MyEngagementsPageProps {
  searchParams: Promise<Record<string, string | undefined>>;
}

export default async function MyEngagementsPage({
  searchParams,
}: MyEngagementsPageProps) {
  const params = await searchParams;
  const unit = params.unit;
  const agent = params.agent;

  const heading = unit
    ? `Engagements for unit: ${unit}`
    : agent
      ? `Engagements for agent: ${agent}`
      : "Your engagements";

  return (
    <div
      className="flex h-full min-h-[40vh] flex-col items-center justify-center gap-3 text-center"
      data-testid="my-engagements-page"
    >
      <MessagesSquare
        className="h-10 w-10 text-muted-foreground"
        aria-hidden="true"
      />
      <h1 className="text-lg font-semibold">{heading}</h1>
      <p className="max-w-md text-sm text-muted-foreground">
        Select an engagement from the list on the left to open the
        conversation, or start a new one with the button at the top right.
      </p>
    </div>
  );
}
