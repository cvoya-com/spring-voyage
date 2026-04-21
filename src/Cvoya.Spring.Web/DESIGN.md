# Spring Voyage Portal — Design System

> **Scope:** `src/Cvoya.Spring.Web/` (the Next.js 16 / React 19 / Tailwind 4 web portal).
>
> **Authority.** This file is the single source of visual truth for the portal. Treat it the same way `AGENTS.md` and `CONVENTIONS.md` are treated — required reading for any change under `src/Cvoya.Spring.Web/`, and something you update in the same PR that changes a visual pattern.
>
> **Format.** Plain, agent-readable markdown following the [Google Stitch `DESIGN.md` spec](https://stitch.withgoogle.com/docs/design-md/overview/). No embedded images; tokens are explicit strings so an agent can paste them.
>
> **Grounded in reality.** Every token and pattern below reflects what is actually in the code today (`src/app/globals.css`, `src/components/ui/*`, `src/app/**`). When the code drifts, update this file — do not invent values that the code does not use.

---

## 1. Overview

The portal is a dark-first operations console for a platform engineer watching agents and units run. The ethos is **calm, terse, and information-dense**:

- A neutral slate/zinc canvas so coloured signals (status, severity, cost) pop without fighting each other.
- A single blue accent (`#3b82f6`) used sparingly — primary actions, active nav, links, focus rings.
- Short lines of copy. No marketing voice. Empty states explain the next action in one sentence.
- Cards and tables over hero sections; lucide line icons at 4×4 or 5×5 for context, never decoration.

The portal is **shadcn-flavoured** (class-variance-authority variants, `cn()` helper, `components/ui/*` primitives) but deliberately trimmed — the platform doesn't pull in Radix for every primitive; a dialog is a focus-trapped `<div>` not a Radix portal. Bias toward the minimal thing that reads well and is keyboard-accessible.

---

## 2. Color palette

Defined as CSS custom properties in [`src/app/globals.css`](src/app/globals.css) under the Tailwind 4 `@theme` block. Dark is the default (root `<body>` carries `dark`; `<html>` gets `"dark"` or `"light"` from `ThemeProvider`). Use the tokens via Tailwind utilities (`bg-background`, `text-muted-foreground`, etc.) — never paste raw hexes into components.

### 2.1 Dark (default)

| Token                           | Hex       | Role                                                                                     |
| ------------------------------- | --------- | ---------------------------------------------------------------------------------------- |
| `--color-background`            | `#09090b` | Page canvas. Also the `themeColor` meta.                                                 |
| `--color-foreground`            | `#fafafa` | Default text.                                                                            |
| `--color-card`                  | `#0a0a0f` | Card, dialog, toast, sidebar surfaces.                                                   |
| `--color-card-foreground`       | `#fafafa` | Text on cards and dialogs.                                                               |
| `--color-popover`               | `#0a0a0f` | Popovers / floating panels (reserved — no popover primitive today).                      |
| `--color-popover-foreground`    | `#fafafa` | Text on popovers.                                                                        |
| `--color-primary`               | `#3b82f6` | Primary brand/action — default buttons, links, active nav, focus ring.                   |
| `--color-primary-foreground`    | `#fafafa` | Text on primary surfaces.                                                                |
| `--color-secondary`             | `#1e1e2e` | Secondary button background, secondary badges.                                           |
| `--color-secondary-foreground`  | `#fafafa` | Text on secondary surfaces.                                                              |
| `--color-muted`                 | `#18181b` | Skeleton loaders, tab list background, inline `<pre>` blocks.                            |
| `--color-muted-foreground`      | `#a1a1aa` | Labels, helper copy, inactive nav, timestamps.                                           |
| `--color-accent`                | `#1e1e2e` | Hover background for ghost / outline buttons and nav items.                              |
| `--color-accent-foreground`     | `#fafafa` | Text on accent hover.                                                                    |
| `--color-destructive`           | `#ef4444` | Destructive button, error badge/severity, delete icon.                                   |
| `--color-border`                | `#27272a` | Borders on cards, inputs, dividers, scrollbar thumb.                                     |
| `--color-input`                 | `#27272a` | Input / select border.                                                                   |
| `--color-ring`                  | `#3b82f6` | Focus ring (same hue as primary).                                                        |
| `--color-success`               | `#22c55e` | Running / healthy / success badges.                                                      |
| `--color-warning`               | `#eab308` | Starting / stopping / degraded / warning severity.                                       |

### 2.2 Light

Applied when `<html>` carries the `.light` class (set by `ThemeProvider` from `localStorage`; SSR defaults to dark). Same token names, re-pointed:

| Token                           | Hex       |
| ------------------------------- | --------- |
| `--color-background`            | `#ffffff` |
| `--color-foreground`            | `#09090b` |
| `--color-card`                  | `#ffffff` |
| `--color-card-foreground`       | `#09090b` |
| `--color-popover`               | `#ffffff` |
| `--color-popover-foreground`    | `#09090b` |
| `--color-primary`               | `#2563eb` |
| `--color-primary-foreground`    | `#ffffff` |
| `--color-secondary`             | `#f4f4f5` |
| `--color-secondary-foreground`  | `#18181b` |
| `--color-muted`                 | `#f4f4f5` |
| `--color-muted-foreground`      | `#71717a` |
| `--color-accent`                | `#f4f4f5` |
| `--color-accent-foreground`     | `#18181b` |
| `--color-destructive`           | `#dc2626` |
| `--color-border`                | `#e4e4e7` |
| `--color-input`                 | `#e4e4e7` |
| `--color-ring`                  | `#2563eb` |
| `--color-success`               | `#16a34a` |
| `--color-warning`               | `#ca8a04` |

### 2.3 Status & severity palette (applied, not tokenised)

Some visual signals use Tailwind's built-in palette directly rather than semantic tokens (see `src/app/page.tsx`). Keep the mapping consistent when you add new status indicators:

| Concept                                      | Applied colour                                     | Example                                 |
| -------------------------------------------- | -------------------------------------------------- | --------------------------------------- |
| Unit status — `Running`                      | `bg-green-500` dot / `text-green-500` icon         | Dashboard unit card.                    |
| Unit status — `Starting` / `Stopping`        | `bg-yellow-500` dot                                | Dashboard unit card.                    |
| Unit status — `Error`                        | `bg-red-500` dot / `text-amber-500` "Degraded" icon | Dashboard health stat + unit row.       |
| Unit status — `Draft` / `Stopped`            | `bg-muted-foreground` dot / `secondary` badge      | Dashboard unit card.                    |
| Activity severity — `Info`                   | `bg-blue-500` / `text-blue-500`                    | Activity feed, dashboard timeline.      |
| Activity severity — `Warning`                | `bg-amber-500` / `text-amber-500` (or `warning`)   | Activity feed.                          |
| Activity severity — `Error`                  | `bg-red-500` / `text-red-500` (or `destructive`)   | Activity feed.                          |
| Activity severity — `Debug`                  | `bg-muted-foreground` / `text-muted-foreground`    | Activity feed.                          |

Prefer the semantic `success` / `warning` / `destructive` tokens on Badge / Button. Use the raw Tailwind palette only for small non-interactive indicators (dots, icons) where the semantic token would look muddy.

---

## 3. Typography

### 3.1 Font family

Declared in `src/app/globals.css`:

```css
body {
  font-family: system-ui, -apple-system, sans-serif;
  -webkit-font-smoothing: antialiased; /* via `antialiased` class on <body> */
}
```

**No `next/font` import today** — the portal deliberately uses the platform system stack for zero layout shift and zero fetch. If the design later calls for a branded typeface, introduce it through `next/font` and document it here before shipping.

### 3.2 Scale

The portal uses the default Tailwind 4 type scale. Observed sizes, in order of frequency:

| Utility       | Size / line-height          | Typical use                                                      |
| ------------- | --------------------------- | ---------------------------------------------------------------- |
| `text-xs`     | 12px / 16px                 | Helper text, timestamps, nav version, badge contents, empty-state body. |
| `text-sm`     | 14px / 20px                 | Body text, table cells, button label, description under H1.       |
| `text-base`   | 16px / 24px                 | Reserved — almost never used directly.                            |
| `text-lg`     | 18px / 28px                 | Sidebar brand, dialog title (`text-lg font-semibold`), section H2s. |
| `text-2xl`    | 24px / 32px                 | Page H1s (`text-2xl font-bold`), stat-card values.                |
| `text-[10px]` | 10px (literal)              | Compact pill badges on the activity timeline.                     |

### 3.3 Weight

Only three weights used: `font-medium` (500), `font-semibold` (600), `font-bold` (700). Regular (400) is the default. Do not introduce heavier weights (`font-extrabold`, `font-black`).

### 3.4 Line height & tracking

- Cards and list rows rely on default line heights — no bespoke `leading-*` except `leading-none` on `CardTitle`.
- `CardTitle` uses `tracking-tight` for the tighter heading rhythm. No other tracking overrides.

---

## 4. Spacing

Tailwind 4 defaults — no customisation. Observed vocabulary (use these; avoid pulling in fresh values):

| Utility         | Pixels | Typical use                                              |
| --------------- | ------ | -------------------------------------------------------- |
| `gap-1` / `p-1` | 4      | Icon–label gap inside badges, tab list padding.          |
| `gap-2` / `p-2` | 8      | Icon–label gap on nav items, buttons, list row padding.  |
| `p-3`           | 12     | Table cell, list row content padding.                    |
| `gap-3` / `p-4` | 16     | Card padding (`p-4`), dashboard grid gutters (`gap-4`).  |
| `gap-6` / `p-6` | 24     | Dialog body padding, section spacing (`space-y-6`), empty-state card. |
| `p-8`           | 32     | "Create your first unit" CTA card padding.               |
| `pt-14`         | 56     | Mobile main pane top padding (clear fixed menu button).  |

Primitives:

- **Page shell.** `<main>` from `AppShell` is `p-4 md:p-6 pt-14 md:pt-6`.
- **Page sections.** `<div className="space-y-6">` wraps the H1 + subsequent content blocks.
- **Sidebar width.** Fixed at `w-56` (224px) on `md+`.
- **Dashboard grids.** `grid-cols-2 gap-4 sm:grid-cols-4` for stats; `grid-cols-1 gap-6 lg:grid-cols-3` for main sections; `grid-cols-1 gap-3 sm:grid-cols-2` for card lists.

---

## 5. Radii

Defined in `globals.css` `@theme`:

| Token          | Value     | Typical use                                       |
| -------------- | --------- | ------------------------------------------------- |
| `--radius-sm`  | `0.25rem` (4px) | Scrollbar thumb, inline chips.              |
| `--radius-md`  | `0.375rem` (6px) | Buttons, inputs, nav items, tabs triggers. |
| `--radius-lg`  | `0.5rem` (8px)  | Cards, dialogs, toasts.                     |
| `rounded-full` | —         | Badges, status dots.                              |

No extra-large radii. If a primitive asks for something rounder than `rounded-lg`, it's almost certainly a `rounded-full` pill.

---

## 6. Shadows

The portal leans on the Tailwind defaults without custom values:

| Utility      | Where it shows up                                            |
| ------------ | ------------------------------------------------------------ |
| `shadow-sm`  | Card surface, input affordance, tabs trigger active state.   |
| `shadow-lg`  | Toast.                                                       |
| `shadow-xl`  | Dialog panel.                                                |

Elevation is kept flat on purpose: a card has a tiny shadow to separate it from the canvas, and only modal-class surfaces (toast, dialog) get a heavier one.

---

## 7. Component patterns

The portal's primitive library lives in `src/components/ui/`. Shared composites live in `src/components/`. When you extend or create a new primitive, add a subsection here.

### 7.1 Buttons — `src/components/ui/button.tsx`

- `class-variance-authority` variants: `default`, `destructive`, `outline`, `secondary`, `ghost`, `link`.
- Sizes: `default` (`h-9 px-4`), `sm` (`h-8 px-3`), `lg` (`h-10 px-8`), `icon` (`h-9 w-9`).
- Always `rounded-md`, `text-sm`, `font-medium`, and a visible focus ring (`focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2`).
- Leading icons are `h-4 w-4` followed by `mr-1`; trailing icons flip the margin.
- Example: `src/app/units/page.tsx` (`<Button><Plus className="h-4 w-4 mr-1" /> New unit</Button>`).

### 7.2 Inputs — `src/components/ui/input.tsx`

- Fixed `h-9`, full width, `rounded-md`, `border border-input bg-background`, `text-sm`.
- Placeholder uses `placeholder:text-muted-foreground`.
- Focus: `focus-visible:ring-1 focus-visible:ring-ring` (thinner than buttons on purpose — inputs live in denser forms).
- Disabled: `disabled:cursor-not-allowed disabled:opacity-50`.
- `<select>` needs no custom primitive — inline it with `h-9 rounded-md border border-input bg-background px-3 text-sm` (see unit detail's scheme picker).

### 7.3 Cards — `src/components/ui/card.tsx`

- `Card`: `rounded-lg border border-border bg-card text-card-foreground shadow-sm`.
- `CardHeader` is `p-4 space-y-1.5`. `CardContent` is `p-4 pt-0`. `CardTitle` is `text-sm font-semibold leading-none tracking-tight`.
- Cards are the default container for every page section. Interactive cards (e.g., unit tile) add `hover:bg-accent/50` or `hover:border-primary/50 hover:bg-muted/30` and wrap the card in a `<Link>`.
- Empty-state pattern: use a Card with `p-6 text-center` (or `p-8 text-center` for the primary-CTA variant), `mx-auto` on the icon, one sentence of body copy, and an optional button or link (`src/app/page.tsx` — "Create your first unit", "Start a unit to see activity here").

### 7.4 Badges — `src/components/ui/badge.tsx`

- Always `rounded-full`, `px-2 py-0.5`, `text-xs font-medium`.
- Variants: `default` (primary-tinted), `success`, `warning`, `destructive`, `secondary`, `outline`.
- Semantic badges (`success`, `warning`, `destructive`) use a 15%-opacity tint of the token as the background with the full-strength token as the text — this keeps them legible on the dark canvas.
- For very tight timelines use `className="text-[10px] px-1.5 py-0"` (see dashboard activity timeline source badge).

### 7.4a Inline alert banners (not a component — a shared pattern)

Used for "this thing needs operator attention" callouts inside wizard steps and tab bodies — e.g. the GitHub install-link banner on `src/Cvoya.Spring.Connector.GitHub/web/connector-wizard-step.tsx` (PR #610) and the LLM provider credential-status indicator on `src/Cvoya.Spring.Web/src/app/units/create/page.tsx` (PR-S2 / #598, extended by #626). Reuse the same token palette so a single axe-clean configuration covers them:

- **Warning** (not-configured, unreachable, deprecated, etc.): `rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-warning`, inner text in `text-foreground` for the body copy. `role="alert"`. Include an actionable control inside the banner whenever possible — either a deep-link, an inline form (the #626 credential input), or a primary button.
- **Success** (configured, inherited, healthy): `rounded-md border border-emerald-500/40 bg-emerald-500/10 px-3 py-2 text-sm text-emerald-900 dark:text-emerald-200`. `role="status"`. A success banner may carry a secondary "Override" button (see `src/Cvoya.Spring.Web/src/app/units/create/page.tsx` `CredentialSection`) rendered as a `<button>` with `underline underline-offset-2` — never a div with `onClick` (axe catches that in the a11y smoke).
- Prefix with the matching lucide icon at `h-4 w-4` (`AlertTriangle` for warning, `CheckCircle2` for success); mark the icon `aria-hidden` since the `role` already conveys the status.

Don't invent new token pairs for these — PR #599 pinned the current tokens as axe AA-clean and the wizard a11y regression tests exercise them directly.

#### 7.4b Inline credential input (#626)

The credential input that appears inside the #626 banner is NOT a generic reusable component — it is tightly coupled to the credential surface. When copying the pattern elsewhere, preserve these invariants:

- The text field defaults to `type="password"` and is paired with a `<Button type="button" variant="outline" size="sm">` that flips it to `text`. The button uses `aria-label="Show <Provider> API key"` / `"Hide ..."` (not icon-only) and sets `aria-pressed` to reflect state. Keyboard focus reaches it via the natural tab order; never build this as a pure-icon div.
- The "Save as tenant default" toggle is a real `<input type="checkbox">` with a matching `<label htmlFor>`, not a custom switch — the simpler primitive avoids the `aria-checked` / keyboard traps that "switch" role implementations tend to collect.
- Plaintext values live in React state only long enough to be POSTed; never persist them, never send them to the status endpoint, never interpolate them into log messages.
- There is **no standalone "Validate" button** (#655). Validation fires automatically on the input's `onBlur` — the operator types/pastes a key, tabs or clicks away, and the wizard posts the key to `POST /api/v1/system/credentials/{provider}/validate`. If the operator pastes and clicks Next before blur settles, the Next handler races the same mutation: while it is pending the Next button disables; when it fails, the wizard stays on Step 1 with the inline error.
- Validation result lives directly under the input, not inside the outer banner. In-flight is `text-xs text-muted-foreground` ("Validating &lt;Provider&gt; API key…"). Success is `text-xs text-emerald-700 dark:text-emerald-300` with a `CheckCircle2` lead. Failure is `text-xs text-destructive` with an `AlertTriangle` lead and the server-supplied operator message verbatim. Editing the key clears the verdict so the next blur re-validates.
- **The Model dropdown is hidden until a live model source exists** (#655). Sources, in priority order: Ollama's local catalog (when the tool is `dapr-agent` + provider `ollama`), the freshly-validated model list from the wizard's validation call, or `providerModels` fetched with a resolvable tenant/unit credential. Before the operator supplies and validates a key — or inherits one from the tenant — the dropdown does not render at all, so operators are never asked to pick from a stale static fallback.

### 7.5 Dialogs — `src/components/ui/dialog.tsx`, `confirm-dialog.tsx`

- In-house modal (no Radix). `role="dialog"`, `aria-modal="true"`, focus trap, ESC closes, backdrop mousedown closes, body scroll locked.
- Panel: `w-full max-w-lg rounded-lg border border-border bg-card p-6 shadow-xl`, with `max-h-[calc(100vh-2rem)] overflow-y-auto`.
- Backdrop: `bg-black/50` at `z-50`.
- `ConfirmDialog` is the canonical wrapper for destructive confirmation — pass `onConfirm`, `onCancel`, `pending`; the destructive button variant is the default.

### 7.6 Tables — `src/components/ui/table.tsx`

- Wrapped in `<div className="relative w-full overflow-auto">` so a narrow viewport scrolls the table horizontally instead of breaking the layout.
- `TableRow` has `border-b border-border transition-colors hover:bg-muted/50`.
- `TableHead`: `h-10 px-3 text-left align-middle font-medium text-muted-foreground`.
- For simple lists (dashboard unit list, members list) use a `<ul className="divide-y divide-border">` inside a Card instead of a full Table.

### 7.7 Tabs — `src/components/ui/tabs.tsx`

- Controlled via `TabsContext` — no Radix. `TabsList` is `inline-flex h-9 rounded-lg bg-muted p-1`. `TabsTrigger` is `rounded-md px-3 py-1 text-sm font-medium`; active state flips to `bg-background text-foreground shadow-sm`.
- Used for unit detail's Agents / Sub-units / Skills / Connector / Secrets / Activity sections and the agent detail page's Interaction / Runtime / Settings / Advanced split (#604).
- **Optional controlled mode.** `<Tabs>` accepts `value` + `onValueChange` for pages that need to reflect the active tab in the URL (e.g. agent detail's `?tab=<interaction|runtime|settings|advanced>` — the caller mirrors `useSearchParams` into the prop and pushes a `router.replace` on change so deep links and browser back/forward stay honest). Omit the props to fall back to the uncontrolled `defaultValue` path used by unit detail.
- **Entity detail pages follow the same pattern.** `/units/[id]` (`unit-config-client.tsx`) and `/agents/[id]` (`agent-detail-client.tsx`) both group their Cards into `<TabsContent>` blocks with a single `<TabsList>` directly below the page header. New entity detail pages should mirror this shape rather than invent a new layout; pick Runtime as the default tab when the page has a "what's happening now?" surface to lead with.

### 7.8 Toast — `src/components/ui/toast.tsx`

- `ToastProvider` at the root. `useToast()` returns a `toast({ title, description?, variant? })` function; variants are `default` and `destructive`.
- Stack: `fixed bottom-4 right-4 z-50 flex flex-col gap-2`.
- Auto-dismiss at 4s; animation `animate-in slide-in-from-bottom-2`.
- Use for every async result that isn't a full page reload — destructive actions especially.

### 7.9 Skeleton — `src/components/ui/skeleton.tsx`

- `animate-pulse rounded-md bg-muted`. Size via `className` (e.g. `h-24`, `h-8 w-32`).
- Pattern: mirror the post-load layout so the page doesn't shift — see `DashboardSkeleton` in `src/app/page.tsx`.

### 7.10 Sidebar & app shell — `src/components/sidebar.tsx`, `src/components/app-shell.tsx`

- Fixed left nav on `md+` (`w-56`, `border-r border-border bg-card`). Mobile slides over a `bg-black/50` backdrop via a `fixed top-3 left-3` menu trigger.
- Nav item: `flex items-center gap-2 rounded-md px-3 py-2 text-sm transition-colors`. Active: `bg-primary/10 text-primary font-medium`. Inactive: `text-muted-foreground hover:bg-accent hover:text-accent-foreground`.
- Brand wordmark is `text-lg font-bold` ("Spring Voyage"). Version chip is `text-xs text-muted-foreground` in the footer, paired with the theme toggle (`Sun` / `Moon` at `h-3.5 w-3.5`).

### 7.11 Cards for domain entities — `stat-card.tsx`, `unit-card.tsx`, `agent-card.tsx`, `conversation-card.tsx`, `inbox-card.tsx`, `activity-feed.tsx`

- Stat card: label (`text-xs text-muted-foreground`) + value (`text-2xl font-bold`) + trailing icon (`text-muted-foreground`).
- Unit / agent / conversation / inbox cards compose the base `Card` with a status dot + name + registered-at row. They are the **only** card layouts allowed for these entity types — pages must not invent bespoke unit/agent/conversation/inbox layouts. Every detail page (`/units/[id]`, `/agents/[id]`, `/conversations/[id]`) must reuse the matching primitive at the top of its summary block (PR-R2 / #392).
- **Whole-card click target (#593).** Every entity card is navigable from any dead-space surface — the primary card `<Link>` stretches across the card via an `::after` absolute overlay on a `relative` `<Card>`. Descendant interactive controls (cross-link icon buttons, delete buttons, footer "Open" links, from-sender links) are promoted to `relative z-[1]` so they keep their own click targets and stay reachable by keyboard. The primary link carries an `aria-label` that names the entity, and the `Card` shows a visible `focus-within` ring so keyboard users see which card holds focus.
- `<AgentCard>` accepts an `actions` prop — a React node rendered next to the card's "Open" link. Use it to surface row-scoped quick actions (edit, remove, mute) without duplicating the card chrome. Membership editor in `app/units/[id]/agents-tab.tsx` is the canonical example (#472).
- Activity feed row: `flex items-start gap-2 text-sm` with a 2×2 severity dot at `mt-1.5`, message on top, meta (`text-xs text-muted-foreground`) below.
- Conversation card (`components/cards/conversation-card.tsx`): MessagesSquare icon + title + status badge on the top row; truncated participants (max 3, `+N more` overflow) and `timeAgo(lastActivityAt)` on the meta row; trailing "Open" link to `/conversations/[id]`.
- Inbox card (`components/cards/inbox-card.tsx`): Inbox icon + summary + `Awaiting you` warning badge on the top row; `From <address>` (cross-linked to `/agents/[id]` or `/units/[id]` when the scheme is `agent://` or `unit://`; `human://` renders as plain monospace) and `timeAgo(pendingSince)` on the meta row; trailing "Open thread" link to `/conversations/[id]`. Drives `/inbox` (#447), which is the portal counterpart of `spring inbox list`.

### 7.11b Multi-rule config tab — `app/units/[id]/boundary-tab.tsx`

Unit boundary configuration (#495) is the canonical **multi-rule editor** layout for tabs that wrap a set of declarative rules. Every new config surface that has the same "N rules across M dimensions" shape should copy this chrome before inventing its own.

- **Summary card on top.** A single card holds the dimension's status (`Transparent` vs `Configured` outline/solid badge), a one-line description, the primary **Save** button, a **Clear all rules** destructive-outline button, and an inline "Unsaved changes" hint.
- **One sub-card per dimension.** Each sub-card carries a lucide icon, the dimension name (`text-sm` title), and a `text-xs text-muted-foreground` pill describing the effect ("hide matching entries", "rewrite matching entries", "collapse matches into a unit-level entry").
- **Rule list as `divide-y` `ul` with monospace rows.** Existing rules render as `font-mono text-xs` single-line summaries so the relevant shape — "domain: X · origin: Y" — is glanceable. Per-row **Trash** icon button (`variant="outline"`, `size="sm"`) removes locally; nothing is persisted until the outer **Save** fires.
- **Add-rule form inside each sub-card.** A nested `rounded-md border border-border p-3` block carries the per-dimension input grid (`grid-cols-1 sm:grid-cols-2`) and a trailing **Add** button that appends to local state and clears the inputs. Required fields are marked with a `text-destructive` asterisk (Synthesis' `name`).
- **Local edits, one PUT.** The entire set is held in local `useState` so the user can stage multiple changes before pressing **Save boundary**; that PUT replaces the whole boundary (matches the CLI's `set` semantics). **Clear all rules** opens the shared `ConfirmDialog` and DELETEs.

### 7.11d Inherit-from-parent indicator — `app/agents/[id]/execution-panel.tsx`

The Agent Execution panel (#601 B-wide — PR landed alongside this entry) introduced a reusable indicator shape for form fields whose blank value is resolved to a parent entity's default at save time. Use this pattern anywhere an editor lets an operator override an inherited value and needs to show what the default currently is:

- **Italic grey placeholder** on the field itself: `placeholder="inherited from unit: ghcr.io/acme/spring-agent:v1"` plus `className="italic text-muted-foreground placeholder:italic placeholder:text-muted-foreground"`. On `<select>`, the first `<option>` carries the same `inherited: …` prefix and the select itself drops into italic grey while the value is null.
- **Help copy below the control** duplicates the value in plain text prefixed with `inherited from unit:` (or the equivalent parent noun). The help row carries `data-testid="inherit-indicator"` so the unit-tests can assert coverage without scraping placeholders out of shadow DOM.
- **No visual lock.** The control stays fully editable — clicking into the field clears the indicator and lets the operator type their own value. Leaving the field blank on save persists `null` on the child; the backend resolves to the parent default at runtime.
- **Server trip.** The indicator needs the parent's own config, so the component fetches both the child and parent blocks via TanStack Query (`useAgentExecution(agentId)` + `useUnitExecution(parentUnitId)`) — keyed by the parent's id so cached reads are shared across any other surface that renders the same parent.
- **A11y.** The placeholder is decorative (contrast is intentionally low so the override intent reads as "not my own value"); the help copy below carries the real text so assistive tech never depends on the placeholder. The card header carries an `Inherits` outline badge when the child has no own declarations, flipping to a solid `Configured` badge once any override is persisted.

Don't reach for this pattern for fields that **must** be filled (e.g. required connector configs) — those use a bordered warning banner like §7.4a. The inherit indicator is specifically for the "blank means inherit" editor contract.

### 7.11c Orchestration tab — `app/units/[id]/orchestration-tab.tsx`

Unit orchestration configuration (#602) follows the multi-card tab pattern from §7.11b but renders two slices rather than N peer dimensions — a read-only strategy selector and an editable label-routing card.

- **Strategy card (read-only today).** Lucide `Workflow` icon + title + `manifest`/`inferred` badge indicating the resolver source. The select renders every key from `ORCHESTRATION_STRATEGIES` (`ai`, `workflow`, `label-routed`) but stays `disabled` until the `/api/v1/units/{id}/orchestration` endpoint lands (#606); a bordered `bg-muted/40` footnote names the manifest-apply workaround and cross-links the follow-up issue.
- **Effective-strategy card.** Single monospace chip for the resolved key followed by a `text-xs text-muted-foreground` explanation of how the resolver got there (manifest key / policy inference / platform default) — mirrors the CLI's one-line "effective policy" output. A second muted line restates the ADR-0010 precedence ladder so operators always know why a given key is active.
- **Label-routing card.** Same row primitives as the boundary tab: `divide-y` `ul` with inline-editable `Input` pairs per rule, per-row trash `Button` (`variant="outline"`, `size="sm"`), and an add-rule grid inside a nested `rounded-md border border-border p-3` block. Two comma-separated inputs below the rule list cover the `AddOnAssign` / `RemoveOnAssign` status-label roundtrip hooks. **Save label routing** and **Clear** ride the existing `/api/v1/units/{id}/policy` endpoint so the CLI (`spring unit policy label-routing set|clear`) and portal round-trip identical payloads.

### 7.12 Conversation thread — `app/conversations/[id]/`, `components/conversation/`

The conversation surface (#410) renders a chat-style thread with role-attributed bubbles and a CLI-shaped composer. Layout primitives:

- **Role bubbles** (`components/conversation/conversation-event-row.tsx`): `human://` is right-aligned in `bg-primary text-primary-foreground`; `agent://` left-aligned in `bg-muted`; `unit://` left-aligned in `bg-muted/60`; `tool` (events of type `DecisionMade`) left-aligned with an amber outline (`bg-amber-50 text-amber-900 border border-amber-200`); `system` left-aligned in `bg-muted/40 italic`.
- **Tool calls and lifecycle events** (`StateChanged`, `WorkflowStepCompleted`, `ReflectionCompleted`) collapse by default with a chevron toggle; messages stay expanded so the thread reads like a chat. The collapsed call-out shows the event type and its summary on a single truncated line.
- **Bubble meta**: role pill (`Badge variant="outline"` at `h-5 px-1.5 text-[10px]`) + monospace source + relative time, mirrored to the same alignment as the bubble.
- **Composer** (`components/conversation/conversation-composer.tsx`): recipient input (monospace, `scheme://path` placeholder) above a textarea. Quick-pick participant pills sit above the input so users can re-target without typing. Submit on click or `⌘/Ctrl+Enter`. Mirrors the two CLI arguments `spring conversation send` takes.
- **Thread shell** (`app/conversations/[id]/conversation-detail-client.tsx`): scrollable thread (`max-h-[60vh] overflow-y-auto`) with `aria-live="polite"` so screen readers announce new events as the SSE stream lands them. Auto-scrolls to the bottom on new event.
- **Cross-links**: the conversation header's `Origin` is a link to `/activity?source=…`, and the activity row's correlation id renders an "Open thread" pill that deep-links to `/conversations/[id]`.

### 7.13a Expertise editor — `src/components/expertise/`

The expertise panels on `/agents/[id]`, `/units/[id]` (Expertise tab) and the tenant-wide `/directory` page share the same list-shaped editor (#486):

- **Editor rows** (`expertise-editor.tsx`) are `rounded-md border border-border p-3` blocks with a four-column grid on `sm+`: name `Input`, level `<select>` (values: `beginner` / `intermediate` / `advanced` / `expert`, or blank for "unspecified"), description `Input`, and a trash `Button variant="ghost" size="icon"`. The level whitelist is defined once in `lib/api/types.ts` (`EXPERTISE_LEVELS`) so the CLI and portal can't drift.
- **Save / Revert** sit at the bottom right, always-visible; they are disabled until the draft differs from the persisted list. Save issues a single full-replacement `PUT` — the server is the source of truth for the new list and we re-seed the cache from its response.
- **Aggregated list** (`unit-expertise-panel.tsx` / `AggregatedExpertiseList`) renders the read-only composition as the same bordered row shape, but each row carries a `depth` outline badge and a `from agent://…` / `from unit://…` link-back in the meta row.
- **Deep-links** follow the cross-link rules in §7.14: `agent://` and `unit://` origins resolve to the matching detail page; other schemes render as plain monospace text.

### 7.13 Breadcrumbs — `src/components/breadcrumbs.tsx`

- Mandatory on any page that is two or more levels deep (`/units/[id]`, `/agents/[id]`, `/conversations/[id]`, `/packages/[name]/templates/[name]`). The crumb trail starts at `Dashboard` (`/`) and ends with the current entity (no `href`, rendered as `aria-current="page"`).
- Use the singular section name as the intermediate label (`Units`, `Agents`, `Conversations`, `Packages`). Intermediate crumbs link to the matching list page.
- Any new top-level destination needs an entry in `src/lib/extensions/defaults.ts` so the sidebar and command palette mirror the breadcrumb hierarchy (PR-R2 / #392).

### 7.14 Cross-link rules

- Activity rows (dashboard timeline + activity feed) deep-link to the most specific destination available, in priority order: conversation (when `correlationId` is set), then `agent://`/`unit://` source detail page, otherwise plain text.
- Conversation participant pills are clickable and resolve to the matching `/agents/{id}` or `/units/{id}` page. Schemes without a portal page (`human://`) render as plain badges.
- See `docs/design/portal-exploration.md` § 3.3 for the full cross-link contract.

### 7.17b Agents lens — `app/agents/page.tsx`

The `/agents` lens (PR-S1 Sub-PR C / #450) is the tenant-wide roster view for agents; a peer surface to `/units` and `/conversations`. It wraps the `GET /api/v1/agents` roster in a filter bar and grouping toggle — there is no new endpoint and the per-agent detail chrome lives on `/agents/[id]` as before.

- **Layout.** Three stacked cards: page header (H1 + Refresh), filter bar, results. The results card renders either a single flat grid of `<AgentCard>` (grouping = flat) or one `section` per owning unit (grouping = unit), each with a `Network` icon, link to `/units/<unit>`, and a count pill.
- **Filter bar.** Free-text search, owning-unit substring, status (`enabled` / `disabled` / any), expertise free-text (runs `POST /api/v1/directory/search` server-side), and grouping toggle. Every control is keyboard-accessible and writes to `?q=…&unit=…&status=…&expertise=…&group=…` via `router.replace`; the CLI round-trips the same view through `spring agent list`, `spring unit members list <unit>`, and `spring directory search <text>`.
- **Quick actions on each card.** The shared `<AgentCard>` (§ 7.11) stays the visual primitive — the lens only passes `actions` to append two cross-links: **Conversation** (`/conversations?participant=agent://<name>`, mirrors `spring conversation list --participant`) and **Deployment** (`/agents/<name>#deployment` anchor on the lifecycle panel, mirrors `spring agent deploy|scale|undeploy|logs`). Both render every card and surface the server's verdict on empty/ephemeral state rather than hiding the affordance.
- **Empty states.** Two variants per § 7.3: a compact "No agents match these filters" text block with a cross-link to `/directory` when the fleet exists, and the full-card "No agents yet" CTA with Units / Directory / Packages buttons plus a `spring agent list · spring unit members add` monospace footer when the fleet is empty.
- **Dropped filters.** Hosting-mode (`ephemeral` / `persistent`) and initiative-level filters are deliberately out of scope until the API surfaces those on the list response and the CLI grows matching flags. Tracked as parity follow-ups (#572 hosting mode, #573 initiative level) — the lens's bar stays "every filter maps to a CLI verb".

### 7.18 Settings hub — `app/settings/page.tsx`, `components/settings/*`

Settings live on a dedicated `/settings` route (SET-hub #862). The legacy in-shell right-aligned drawer (`components/settings-drawer.tsx`) was retired in IA-appshell (#896) and deleted in SET-drop-drawer (#867). The extension seam carries over: every tenant panel still comes from the drawer-panel registry, so the hosted build can add tenant secrets, members / RBAC, SSO, etc. without patching OSS files — only the surface changed.

- **Surface.** `/settings` renders the four tenant-panel cards (Tenant budget, Tenant defaults, Account, About) inline inside `<Card>`s plus a tile grid of links into the Settings subpages (`/settings/skills`, `/settings/packages`, `/settings/agent-runtimes`, `/settings/system-configuration`).
- **Panel card.** Each panel renders as a `rounded-lg border border-border bg-background p-4` section with a `text-sm font-semibold` title (icon + label), optional `text-xs text-muted-foreground` description, and the panel's own body below. Panels stack vertically with `space-y-4`.
- **Extension contract.** Panels register via `registerExtension({ drawerPanels: [...] })` (see `src/lib/extensions/README.md`) and are consumed on the hub via `useDrawerPanels()`. Each panel declares `id`, `label`, `icon`, `orderHint`, optional `permission`, optional `description`, and a `component: ReactNode`. OSS ships Budget / Auth / About as defaults in `src/lib/extensions/defaults.tsx`. Panels with a `permission` that the active auth adapter rejects disappear silently — OSS's default adapter grants every permission. A hosted extension can replace a default panel by re-using its `id`. The registry key stays `drawerPanels` for backwards compatibility with hosted extensions; the name no longer implies a drawer surface.
- **CLI parity rule.** Every interactive control in any panel MUST have a matching CLI verb. Budget panel ↔ `spring cost set-budget`; About panel ↔ `spring platform info`; Auth panel's token list ↔ `spring auth token list`. Panels whose interactive controls lack a CLI are dropped and a CLI follow-up is filed first.
- **Follow-up ADR.** The drawer-panel extension slot pattern (contract, ordering, CLI-parity rule) will be recorded in a forthcoming ADR (#556).

### 7.15 Icons

- [`lucide-react`](https://lucide.dev). Sizes: `h-3 w-3` (inline meta), `h-3.5 w-3.5` (theme toggle), `h-4 w-4` (button icon, card section icon, severity dot wrapper), `h-5 w-5` (sidebar mobile menu, page H1 icon), `h-10 w-10` (empty-state icon).
- Icons in CTAs never carry colour — they inherit `currentColor` from the surrounding text.
- **Decorative icons carry `aria-hidden="true"`.** Any `lucide-react` glyph that sits next to its own text label (card title icons, severity dots, H1 icons, button leading/trailing icons) should be hidden from the accessibility tree so screen readers don't announce "graphic + label". Icon-only buttons still need a visible-but-hidden `aria-label` on the `<button>` / `<Link>`.

### 7.16 Accessibility — `src/app/globals.css`, `src/components/sidebar.tsx`, `src/test/a11y.ts`

The portal targets **WCAG 2.1 AA** (§ 7 of `docs/design/portal-exploration.md`). Add new surfaces with these constraints already satisfied:

- **Skip link.** Every page starts with a visually-hidden "Skip to main content" anchor that targets `#main-content` — the `<main>` landmark in `AppShell`. The link pops back on-screen on focus (see `src/components/sidebar.tsx`); don't remove or re-parent it.
- **Landmarks.** `AppShell` renders `<main id="main-content" tabIndex={-1}>`. Page roots should not introduce a second `<main>`; they use `<section>` / `<nav>` as appropriate.
- **One `<h1>` per page.** The H1 matches the sidebar label. Section titles are `<h2>`; card titles use `<h3>` via `CardTitle`. Do not re-use `<h1>` for sub-sections.
- **Icon-only buttons need `aria-label`.** The sidebar theme toggle, the sidebar open / close buttons, every `<Button size="icon">`, and the breadcrumb close / remove affordances all carry a concise label describing the action (`"Switch to dark mode"`, `"Remove agent://alpha/one"`). The icon glyph itself is `aria-hidden="true"`.
- **`aria-expanded` / `aria-controls` on disclosures.** The mobile sidebar trigger uses `aria-expanded` against its drawer; settings opener uses `aria-haspopup="dialog"`. Mirror this on any new toggle.
- **Tab primitives.** `src/components/ui/tabs.tsx` exposes WAI-ARIA roles (`tablist` / `tab` / `tabpanel`) with `aria-selected`, `aria-controls`, roving `tabindex`, and arrow-key / Home / End navigation. Callers get this for free by composing `<Tabs>` / `<TabsList>` / `<TabsTrigger>` / `<TabsContent>`.
- **Live regions.** The shared `<ActivityFeed>` renders with `role="log" aria-live="polite" aria-relevant="additions"` so screen readers announce new SSE entries without re-announcing the whole feed. Conversation threads use the same pattern (`aria-live="polite"` on the scroll container).
- **Focus management.** `Dialog` and other modal overlays move focus into the panel on open, trap `Tab` / `Shift+Tab` inside, and return focus to the opener on close. New overlays must preserve this contract.
- **Forms.** Every `<input>` / `<select>` / `<textarea>` has either a wrapping `<label>` or an explicit `aria-label`. Placeholders are never the only label. The create-unit wizard and the activity page are the canonical examples.
- **Reduced motion.** `src/app/globals.css` ships a `@media (prefers-reduced-motion: reduce)` block that drops animation / transition durations to ≈0. Never override `animate-*` classes on critical-path elements with inline styles that bypass this guard.
- **Regression harness.** Smoke specs in `src/test/a11y-routes.test.tsx` run axe-core (via `vitest-axe`) against every top-level route and the shared shell primitives. Any new sidebar entry needs a matching `it(…)` that calls `expectNoAxeViolations(container)`. Contrast + rendered-geometry rules (`color-contrast`, `scrollable-region-focusable`) are disabled because JSDOM cannot compute styles — the DESIGN.md § 2 token locks plus the responsive pass cover those manually.

---

## 8. Voice & tone

Match the copy voice already in the product:

- **Imperative, not descriptive.** "Create your first unit." "Start a unit to see activity here." Not "You can create units…".
- **No marketing.** No emoji in UI strings, no exclamation marks, no adjectives like "powerful" / "beautiful" / "seamless".
- **Be short.** H1s are two words when possible. Empty-state body copy is one sentence.
- **Be literal.** Use domain nouns verbatim (`unit`, `agent`, `connector`, `skill`, `initiative`, `budget`). Never euphemise — "delete" is "Delete", not "Remove" (unless the semantics are actually "remove from list"). Never invent synonyms for concepts that are documented in `docs/architecture/`.
- **Units in UI match CLI vocabulary.** When naming a portal action, check `spring <verb>` in the CLI first; parity is mandatory (`AGENTS.md` UI/CLI parity rule).

Example approvals (already in the product):

- ✅ "Registered units in this environment."
- ✅ "No members"
- ✅ "Agents appear when you create a unit from a template."

Example rejections:

- ❌ "Welcome to your Spring Voyage dashboard! Get started by creating a powerful new unit."
- ❌ "Oh no! Something went wrong. 😢"

---

## 9. Dark mode

Dark mode is the default and the canonical look. `src/app/layout.tsx` renders `<body className="… dark">`; `ThemeProvider` hydrates from `localStorage` (`spring-voyage-theme`, values `dark` or `light`) and keeps `<html>` in sync via `document.documentElement.className`. The toggle lives in the sidebar footer.

Implications:

- Every token in §2.1 has a §2.2 counterpart. Never hardcode a hex in a component — always reach through a token so the light theme also works.
- When you add a raw Tailwind palette colour (e.g. `bg-red-500`), the same value is used in both themes. Check that it passes contrast in both (this is fine for the dots and icons we use today).
- `<meta name="theme-color">` is pinned to the dark canvas (`#09090b`) — keep it in sync with `--color-background` (dark) rather than the current theme.

---

## 10. Updating this file

Update `DESIGN.md` in the same PR that introduces, modifies, or removes a visual pattern in `src/Cvoya.Spring.Web/`. Examples that require an update:

- A new colour token, or an existing token re-pointing.
- A new component variant (a new Button variant, a new Badge variant).
- A new composite (empty state, data table) that other pages should copy.
- A radius, shadow, or spacing change that affects multiple surfaces.

Examples that do **not** require an update:

- Copy tweaks within an existing voice/tone pattern.
- Swapping one existing token for another semantically equivalent one in a single file.
- Per-page layout changes that don't define a new reusable shape.

If you're unsure, err on the side of recording the pattern. This file is meant to be edited.
