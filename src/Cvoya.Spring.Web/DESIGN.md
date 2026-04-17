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
- Used for unit detail's Agents / Sub-units / Skills / Connector / Secrets / Activity sections.

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

### 7.11 Cards for domain entities — `stat-card.tsx`, `unit-card.tsx`, `agent-card.tsx`, `activity-feed.tsx`

- Stat card: label (`text-xs text-muted-foreground`) + value (`text-2xl font-bold`) + trailing icon (`text-muted-foreground`).
- Unit / agent cards compose the base `Card` with a status dot + name + registered-at row.
- Activity feed row: `flex items-start gap-2 text-sm` with a 2×2 severity dot at `mt-1.5`, message on top, meta (`text-xs text-muted-foreground`) below.

### 7.12 Icons

- [`lucide-react`](https://lucide.dev). Sizes: `h-3 w-3` (inline meta), `h-3.5 w-3.5` (theme toggle), `h-4 w-4` (button icon, card section icon, severity dot wrapper), `h-5 w-5` (sidebar mobile menu, page H1 icon), `h-10 w-10` (empty-state icon).
- Icons in CTAs never carry colour — they inherit `currentColor` from the surrounding text.

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
