import { type ClassValue, clsx } from "clsx";
import { twMerge } from "tailwind-merge";

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

/**
 * #989: Map a raw activity event-type identifier to a short,
 * user-friendly label. Raw identifiers (`ConversationStarted`,
 * `MessageReceived`, etc.) are fine for audit logs but bleed
 * technical detail into the user-facing activity feed.
 *
 * The mapping covers every `ActivityEventType` value; an unknown
 * string falls back to the raw value so new server-side event types
 * do not silently break rendering before the client is updated.
 */
export function humanEventType(eventType: string): string {
  const MAP: Record<string, string> = {
    MessageReceived: "Message received",
    MessageSent: "Message sent",
    ThreadStarted: "Thread started",
    ThreadCompleted: "Thread completed",
    DecisionMade: "Decision made",
    ErrorOccurred: "Error",
    StateChanged: "State changed",
    InitiativeTriggered: "Initiative triggered",
    ReflectionCompleted: "Reflection completed",
    WorkflowStepCompleted: "Workflow step completed",
    CostIncurred: "Cost incurred",
    TokenDelta: "Tokens used",
    ValidationProgress: "Validation progress",
  };
  return MAP[eventType] ?? eventType;
}

export function timeAgo(iso: string): string {
  const now = Date.now();
  const then = new Date(iso).getTime();
  const diff = Math.floor((now - then) / 1000);
  if (diff < 60) return `${diff}s ago`;
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
  return `${Math.floor(diff / 86400)}d ago`;
}

export function formatCost(usd: number): string {
  return `$${usd.toFixed(2)}`;
}
