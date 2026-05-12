# 0005 — Web portal runs in Next.js `standalone` mode

- **Status:** Accepted — portal runs with `output: "standalone"`; static-export workarounds are removed.
- **Date:** 2026-04-17
- **Closes:** [#436](https://github.com/cvoya-com/spring-voyage/issues/436)
- **Supersedes:** [ADR 0001](0001-web-portal-rendering-strategy.md) — Web portal rendering strategy (static export vs SSR)
- **Related code:** `src/Cvoya.Spring.Web/next.config.ts`, `src/Cvoya.Spring.Web/src/app/units/[id]/page.tsx`, `src/Cvoya.Spring.Web/src/app/agents/[id]/page.tsx`, `devops/build/Dockerfile`

## Context

ADR 0001 chose `output: "export"` for the web portal on the theory that every screen was a thin client over the REST API and nothing needed to render on the server. That record also listed explicit revisit criteria: per-request personalisation, streaming large responses, or a hosting model that already includes a Node runtime.

Between then and now the picture changed on every one of those axes:

- The portal redesign (#434) introduces streaming activity and conversation views — long-running responses where the server progressively flushes chunks rather than waiting for a complete payload. Static-exported pages cannot produce a streaming response.
- The redesign also introduces per-request personalisation in the shell (tenant-aware nav, user-aware extension slots). That is exactly the "authenticated landing surface that must render differently per tenant before the SPA hydrates" case ADR 0001 called out as a revisit trigger.
- The deployment topology already ships a Node runtime. `devops/build/Dockerfile` builds Next.js in `standalone` mode and runs `node server.js`; the static-export hosting story ADR 0001 argued for was never actually wired up end-to-end.
- `next.config.ts` was switched to `output: "standalone"` in practice before this record caught up. The `generateStaticParams` + `__placeholder__` scaffolding in `/units/[id]` and `/agents/[id]` had been dead code — the build no longer required it, and the guard in the client only protected against a route that the standalone server would never produce.

With `standalone` output, Next.js:

1. Renders server components per request (still no per-screen server data dependency on REST today, but the runtime is there when it's needed).
2. Supports streaming responses via React Server Components and route handlers.
3. Handles arbitrary dynamic segments (`/units/<real-id>`) without a build-time enumeration.
4. Lets the portal add route handlers (API-adjacent endpoints, webhooks) directly under `src/app/` when a feature needs one.

## Decision

**Keep `output: "standalone"` as the portal's rendering mode.** Delete the static-export scaffolding (`generateStaticParams`, `__placeholder__` guards, matching source comments) across the tree. Rely on the Node runtime that the container image already ships.

### What changes with this decision

- **Dynamic routes** (`/units/[id]`, `/agents/[id]`, and any future `[id]` routes) no longer export `generateStaticParams`. The server component under `page.tsx` awaits the `params` Promise and passes the id to the client component.
- **Client components** under dynamic routes stop guarding for `id === "__placeholder__"`. They render for whatever id the URL holds.
- **Streaming** is available for activity and conversation views. Implementations under the portal redesign can lean on React Server Components, `Suspense` boundaries, and route handlers that return `ReadableStream`.
- **Route handlers** under `src/app/.../route.ts` are now a supported extension point. Connector packages and the private cloud repo can add portal-local endpoints without standing up a parallel service.
- **Hosting contract.** The portal ships as a Node service (`node server.js` inside the existing container image). Static-host / CDN fronting remains an option for the non-dynamic asset bundle (`/_next/static/*`, `/public/*`), but the HTML shell is served by the Node runtime. The "rewrite unknown `[id]` paths back to the shell" step from ADR 0001 is obsolete.

### Why not reopen static-export

- The redesign's streaming and per-request personalisation requirements are load-bearing — they cannot be satisfied by a static bundle without adding a second runtime or a client-side workaround that duplicates the server's job.
- The OSS deployability argument from ADR 0001 (static assets deploy anywhere) is still supported in principle — a self-hoster who rejects the Node runtime can front the static assets with their own shell — but that path is now an explicit fork rather than the default. The default targets the container image that operators already run.
- Every revisit criterion from ADR 0001 fired. The decision record was explicit about what would change its answer, and those conditions are now true.

## Consequences

- **Build output is larger.** `standalone` emits a server bundle and trace-collected `node_modules`. This is already reflected in the existing container image and CI.
- **Operational story is Node-first.** The serving layer is `node server.js`, not a generic static host. Self-hosters who want a pure static deployment maintain their own wrapper — explicit, documented fork rather than the framework hiding it.
- **Per-request code is allowed to exist.** Server components, streaming responses, route handlers — all now in scope. The portal redesign issues (#437, #438, #447, etc.) are unblocked on this decision.
- **Hosting-contract rewrite rules** (the `try_files` / CloudFront Function steps in ADR 0001) are obsolete. Removing references from any deployment notes is a follow-up mechanical edit, not a behaviour change.

## Revisit criteria

Reopen this decision when any of the following is true:

1. **Node runtime becomes a hard constraint we cannot pay.** If a large class of self-hosters insist on a Node-free deployment and the CDN-only story wins the cost/benefit argument, revisit whether the server-only features (streaming, route handlers) are replaceable by a client-only implementation or by a separate service.
2. **Edge-runtime portability becomes a platform goal.** If the portal must run on an edge runtime (Cloudflare Workers, Vercel Edge) rather than Node, re-evaluate whether `standalone` is the right output or whether the edge-specific target is a better fit.
3. **The portal grows a non-trivial set of route handlers.** At that scale, consider whether those endpoints belong in the portal or in the API Host. The current decision says "portal-local endpoints are fine when they're portal-specific"; a proliferation changes the calculus.

## Priority

Shipped as part of the portal redesign foundation (PR-F1 of #462). This is the prerequisite for streaming-enabled views (#437, #438) and the per-request extension-slot work (#440).
