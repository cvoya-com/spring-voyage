// Shared placeholder shell for nav entries whose destination page has
// not been built yet. The sidebar can point at the route without the
// user landing on a 404 — and the tracking issue link tells a curious
// operator where follow-up work lives.
//
// Usage:
//
//   <RoutePlaceholder
//     title="Inbox"
//     description="Conversations awaiting a response from you."
//     tracking={[{ number: 447, label: "Inbox surface" }]}
//   />
//
// The nav restructure (#444 / `docs/design/portal-exploration.md` § 3.2)
// introduces several routes ahead of their full implementation. Each
// placeholder surfaces as a single Card so the empty-state pattern from
// DESIGN.md § 7.3 holds.

import Link from "next/link";
import type { LucideIcon } from "lucide-react";
import { ExternalLink, Hammer } from "lucide-react";

import { Card, CardContent } from "@/components/ui/card";

export interface PlaceholderTrackingRef {
  /** GitHub issue number on cvoya-com/spring-voyage. */
  number: number;
  /** Short label describing the tracked work. */
  label: string;
}

export interface RoutePlaceholderProps {
  /** H1 text — usually matches the sidebar label. */
  title: string;
  /** One-line description of what the surface will do. */
  description: string;
  /** Optional icon — defaults to a hammer. */
  icon?: LucideIcon;
  /** Tracking issues (rendered as links on cvoya-com/spring-voyage). */
  tracking?: readonly PlaceholderTrackingRef[];
  /**
   * Optional related routes the user can visit today to get a subset of
   * this surface's functionality. Rendered as plain links.
   */
  related?: readonly { href: string; label: string }[];
}

const REPO = "https://github.com/cvoya-com/spring-voyage";

export function RoutePlaceholder({
  title,
  description,
  icon,
  tracking = [],
  related = [],
}: RoutePlaceholderProps) {
  const Icon = icon ?? Hammer;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold">{title}</h1>
        <p className="text-sm text-muted-foreground">{description}</p>
      </div>

      <Card>
        <CardContent className="space-y-4 p-8 text-center">
          <Icon className="mx-auto h-10 w-10 text-muted-foreground" />
          <div className="space-y-2">
            <p className="text-sm">Not yet implemented in the portal.</p>
            {tracking.length > 0 && (
              <p className="text-xs text-muted-foreground">
                Tracking:{" "}
                {tracking.map((ref, index) => (
                  <span key={ref.number}>
                    <Link
                      href={`${REPO}/issues/${ref.number}`}
                      target="_blank"
                      rel="noreferrer"
                      className="inline-flex items-center gap-1 text-primary hover:underline"
                    >
                      #{ref.number} {ref.label}
                      <ExternalLink className="h-3 w-3" />
                    </Link>
                    {index < tracking.length - 1 ? ", " : ""}
                  </span>
                ))}
              </p>
            )}
          </div>
          {related.length > 0 && (
            <div className="pt-2 text-xs text-muted-foreground">
              <span>In the meantime: </span>
              {related.map((link, index) => (
                <span key={link.href}>
                  <Link
                    href={link.href}
                    className="text-primary hover:underline"
                  >
                    {link.label}
                  </Link>
                  {index < related.length - 1 ? " · " : ""}
                </span>
              ))}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
