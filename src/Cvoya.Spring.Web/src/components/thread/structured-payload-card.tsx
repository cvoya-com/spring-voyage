"use client";

// Collapsed card for a structured payload extracted from a message body
// (#2128). Sits below the chat bubble in `<ThreadEventRow>` when the body
// starts with a JSON envelope (a hallucinated tool-call result the LLM
// emitted as part of its natural-language reply). Shape-only — we do not
// validate the payload against any tool-result schema; that's an SDK-side
// concern.
//
// Visual vocabulary mirrors `<ThreadEventCard>` (§16.6.2): rounded-md
// border, click-to-expand, font-mono technical panel underneath. We do
// **not** reuse `<ThreadEventCard>` directly because that component is
// shaped around `ThreadEvent` semantics (event id, type, severity,
// source attribution) — none of which apply to a free-floating payload
// peeled out of a body.

import { useState } from "react";
import { Braces, ChevronDown, ChevronRight } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

export interface StructuredPayloadCardProps {
  /** The parsed JSON object to render inside the expanded panel. */
  payload: Record<string, unknown>;
  /**
   * Short human-readable label shown next to the chevron in the
   * collapsed state. Defaults to "Structured payload" — a deliberately
   * generic label since shape-only detection cannot promise the
   * envelope is a real tool result.
   */
  label?: string;
  /**
   * Test-id prefix for the card root + interactive elements.
   * Defaults to `structured-payload`.
   */
  testIdPrefix?: string;
  /**
   * Force the technical panel open on first render. Used by tests; the
   * default is collapsed so the bubble's prose stays visually dominant.
   */
  defaultExpanded?: boolean;
}

/**
 * Pretty-print a JSON object with stable two-space indentation.
 * Centralised so the on-screen text matches snapshot/test expectations
 * exactly. We do not attempt syntax highlighting here — see #2128 scope
 * note: the issue gates it on "if cheap to do" and the cheapest options
 * still pull a sizeable highlighter dependency. Tracked in #2131.
 */
function formatJson(payload: Record<string, unknown>): string {
  return JSON.stringify(payload, null, 2);
}

export function StructuredPayloadCard({
  payload,
  label = "Structured payload",
  testIdPrefix = "structured-payload",
  defaultExpanded = false,
}: StructuredPayloadCardProps) {
  const [expanded, setExpanded] = useState(defaultExpanded);
  const formatted = formatJson(payload);

  return (
    <section
      aria-label={label}
      data-testid={testIdPrefix}
      className={cn(
        "rounded-md border border-border bg-muted/40 px-3 py-2 text-sm shadow-sm",
      )}
    >
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        aria-expanded={expanded}
        aria-controls={`${testIdPrefix}-body`}
        data-testid={`${testIdPrefix}-toggle`}
        className="flex w-full items-center gap-2 text-left focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring rounded"
      >
        {expanded ? (
          <ChevronDown
            className="h-3.5 w-3.5 shrink-0 text-muted-foreground"
            aria-hidden="true"
          />
        ) : (
          <ChevronRight
            className="h-3.5 w-3.5 shrink-0 text-muted-foreground"
            aria-hidden="true"
          />
        )}
        <Braces
          className="h-3.5 w-3.5 shrink-0 text-muted-foreground"
          aria-hidden="true"
        />
        <Badge variant="outline" className="h-5 px-1.5 text-[10px]">
          {label}
        </Badge>
      </button>

      {expanded && (
        <pre
          id={`${testIdPrefix}-body`}
          data-testid={`${testIdPrefix}-body`}
          className="mt-2 overflow-x-auto rounded border border-border/60 bg-background/60 p-2 font-mono text-[11px] text-muted-foreground"
        >
          {formatted}
        </pre>
      )}
    </section>
  );
}
