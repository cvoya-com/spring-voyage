"use client";

// Two-option control for the `system_prompt_mode` slot exposed by the
// unit + agent execution blocks (#2691 / #2692 / #2694 — the prompt
// pipeline series under #2667).
//
// The control is intentionally surface-agnostic. Both call sites — the
// unit Execution tab and the agent Execution panel — render the same
// pair of buttons plus a cascade indicator pill. The parent owns the
// persistence wire: unit-side writes go through `setUnitExecution`
// (PUT, partial-update), agent-side writes go through PATCH
// `updateAgentMetadata` so the optional `null` clear path works
// (PUT can only set, never clear, per `PickNonBlank` semantics).
//
// Design vocabulary borrows the create-unit wizard's parent-choice
// radio pattern (DESIGN.md § 12.12 / #814): two `<button>` affordances
// in a `flex gap-3` row, `rounded-md border px-3 py-2 text-sm`,
// `border-primary bg-primary/10 text-primary` for the selected state,
// `border-border bg-muted/30 text-foreground/70` for the unselected
// state, `aria-pressed` per button, dark-mode tokens inherited from
// the existing palette. No new colour tokens.

import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";

export type SystemPromptMode = "append" | "replace";

/**
 * Cascade source for the effective value rendered alongside the
 * control. Mirrors the resolver chain documented in the API surface —
 * Agent → Unit → built-in default (`append`).
 */
export type SystemPromptModeOrigin = "agent" | "unit" | "default";

export interface SystemPromptModeControlProps {
  /**
   * Effective value after the cascade — what the dispatcher will hand
   * to the launcher at runtime. Sourced from
   * `AgentResponse.systemPromptMode` (post-cascade) on the agent
   * surface, or from `UnitExecutionResponse.systemPromptMode ?? "append"`
   * on the unit surface.
   */
  effective: SystemPromptMode;

  /**
   * Where the effective value was sourced from. Drives the cascade
   * indicator pill ("Set here" / "Inherited from unit" / "Default").
   */
  origin: SystemPromptModeOrigin;

  /**
   * Persist a new mode. The parent translates this into the
   * surface-appropriate PATCH/PUT call. The control just owns the
   * UX shape.
   */
  onChange: (next: SystemPromptMode) => void;

  /**
   * Clear the override at the current surface. On the agent panel this
   * issues a PATCH with explicit JSON `null` (`UpdateAgentMetadataRequest`
   * tri-state); on the unit panel the action is omitted because PUT
   * cannot clear individual fields.
   */
  onClear?: () => void;

  /**
   * Disable interaction while a mutation is in flight.
   */
  busy?: boolean;

  /**
   * `agent` surface labels the cascade source as "Inherited from unit"
   * / "Set here" / "Default"; `unit` surface uses "Set here" / "Default"
   * (no upstream cascade above the unit slot itself).
   */
  surface: "agent" | "unit";

  /**
   * Optional `data-testid` prefix so the agent + unit surfaces can
   * disambiguate selectors in tests / e2e.
   */
  testIdPrefix?: string;
}

const MODE_OPTIONS: readonly {
  id: SystemPromptMode;
  short: string;
  long: string;
}[] = [
  {
    id: "append",
    short: "Append",
    long: "Append to default Claude Code prompt (engineer-style agents)",
  },
  {
    id: "replace",
    short: "Replace",
    long: "Replace default Claude Code prompt (non-coding agents, e.g. routers, PMs)",
  },
];

export function SystemPromptModeControl({
  effective,
  origin,
  onChange,
  onClear,
  busy,
  surface,
  testIdPrefix,
}: SystemPromptModeControlProps) {
  const prefix = testIdPrefix ?? "system-prompt-mode";
  const indicator = cascadeLabel(origin, surface);

  return (
    <div className="space-y-2" data-testid={`${prefix}-control`}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <span className="text-sm text-muted-foreground">System prompt mode</span>
          <Badge
            variant="outline"
            className={
              "text-[10px] " +
              (origin === "agent" || origin === "unit"
                ? ""
                : "text-muted-foreground")
            }
            data-testid={`${prefix}-cascade-indicator`}
            data-origin={origin}
          >
            {indicator}
          </Badge>
        </div>
        {onClear && origin === "agent" && (
          <Button
            size="sm"
            variant="ghost"
            onClick={onClear}
            disabled={busy}
            className="h-7 px-2 text-xs"
            aria-label="Clear system prompt mode override"
            data-testid={`${prefix}-clear`}
          >
            Clear override
          </Button>
        )}
      </div>
      <div
        role="radiogroup"
        aria-label="System prompt mode"
        className="flex flex-wrap gap-3"
      >
        {MODE_OPTIONS.map((option) => {
          const selected = effective === option.id;
          return (
            <button
              key={option.id}
              type="button"
              role="radio"
              aria-checked={selected}
              aria-label={option.long}
              disabled={busy}
              onClick={() => {
                if (selected) return;
                onChange(option.id);
              }}
              data-testid={`${prefix}-option-${option.id}`}
              className={
                "min-w-[8rem] flex-1 rounded-md border px-3 py-2 text-left text-sm transition-colors " +
                "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring " +
                "disabled:cursor-not-allowed disabled:opacity-50 " +
                (selected
                  ? "border-primary bg-primary/10 text-primary"
                  : "border-border bg-muted/30 text-foreground/70 hover:border-primary/40 hover:bg-accent/50")
              }
            >
              <span className="block text-sm font-medium">{option.short}</span>
              <span className="mt-0.5 block text-[11px] text-muted-foreground">
                {option.long}
              </span>
            </button>
          );
        })}
      </div>
      <p
        className="text-[11px] text-muted-foreground"
        data-testid={`${prefix}-help`}
      >
        Controls how the assembled system prompt composes with the
        agent runtime&rsquo;s built-in scaffolding.{" "}
        {surface === "agent"
          ? "Leave unset on the agent to inherit the unit default; the unit default itself falls back to Append."
          : "Member agents inherit this default; an agent can override it on its own Execution panel."}{" "}
        <a
          href="https://github.com/cvoya-com/spring-voyage/blob/main/docs/architecture/agent-runtime.md#system-prompt-delivery-and-system_prompt_mode"
          className="text-primary underline-offset-2 hover:underline"
          target="_blank"
          rel="noopener noreferrer"
          data-testid={`${prefix}-docs-link`}
        >
          Learn more about system-prompt modes
        </a>
        .
      </p>
    </div>
  );
}

function cascadeLabel(
  origin: SystemPromptModeOrigin,
  surface: "agent" | "unit",
): string {
  switch (origin) {
    case "agent":
      return "Set here";
    case "unit":
      return surface === "agent" ? "Inherited from unit" : "Set here";
    case "default":
      return "Default";
  }
}
