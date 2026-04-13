# 0001 — Web portal rendering strategy (static export vs SSR)

- **Status:** Accepted — keep `output: "export"` (pure static export).
- **Date:** 2026-04-13
- **Closes:** [#135](https://github.com/savasp/spring-voyage/issues/135)
- **Related code:** `src/Cvoya.Spring.Web/next.config.ts`, `src/Cvoya.Spring.Web/src/app/units/[id]/page.tsx`, `src/Cvoya.Spring.Web/src/app/agents/[id]/page.tsx`

## Context

The web portal (`src/Cvoya.Spring.Web`) is a Next.js app configured with `output: "export"`. It is a thin UI over the API Host — every screen fetches data from the REST API at runtime; no page needs data at render time.

Wave 4b added `/units/[id]` and `/agents/[id]` dynamic routes. Next's static exporter requires every dynamic segment to be enumerated at build time via `generateStaticParams`. Because unit and agent ids are created by users at runtime, the routes each emit a single synthetic `__placeholder__` entry and the detail client short-circuits when it sees that id. The consequence: in-app `Link` navigation works (the client reads the real id from `useParams` at runtime), but a **direct hard load** of `/units/<real-id>` or `/agents/<real-id>` is not served by the exported bundle unless the hosting layer maps unknown paths back to the shell.

The decision to finalise: stay on static export, switch to SSR, or go hybrid (SSR only for dynamic routes).

## Decision

**Keep `output: "export"`.** The portal continues to ship as a static asset bundle that any CDN / static host / object store can serve. Dynamic detail routes continue to use the `__placeholder__` pattern, plus a route-level fallback (below) so hard loads work without a Node runtime.

### Why not SSR / hybrid

- **No server-side data dependency.** Every detail page fetches from the API on mount using the caller's own auth context. There is nothing the server could prerender that the browser couldn't — SSR would add a Node runtime without improving the rendered output.
- **OSS deployability.** The OSS platform targets local-dev + self-host. Static assets deploy to anything (nginx, S3, a CDN, a directory inside the existing container image). Adding an SSR requirement forces every self-hoster onto a Node process with its own lifecycle, logging, and scaling story.
- **Private-repo extension stays clean.** The private repo (Spring Voyage Cloud) layers auth and tenancy via the API Host middleware, not via the web tier. Keeping the web tier as static assets means the cloud deployment can put a CDN in front without coordinating with a Node runtime.
- **Cost of the workaround is bounded.** The `__placeholder__` pattern is a three-line function per dynamic route plus a guard in the client. Cheap, local, obvious from the file.

### Hosting contract for hard-loads

Because hard loads of an arbitrary `/units/<id>` or `/agents/<id>` URL aren't prerendered, the serving layer must rewrite **unknown paths inside a known `[id]` tree to the route's exported shell**, which then reads the id from the URL at runtime. Concretely:

- **Local dev (`next dev`).** Unaffected — dev server handles dynamic routes natively.
- **Static host / CDN.** Configure a rewrite rule so `/units/*` → `/units/[id].html` (and likewise `/agents/*`). For nginx this is `try_files $uri $uri.html /units/[id].html =404;`. For S3/CloudFront, use a CloudFront Function or Lambda@Edge that rewrites unknown segments to the shell. The existing deployment topology (documented in `docs/architecture/cli-and-web.md` and `deployment/Dockerfile`) is responsible for this mapping.
- **Unknown ids.** The client already renders a "not found" state when the API returns 404 for the id. No extra work needed once the rewrite is in place.

The `deployment/Dockerfile` comment claiming the build expects `output: "standalone"` is stale and will be corrected as follow-up tidy-up — it is not part of this decision.

## Consequences — convention for future dynamic routes

All new dynamic routes under `src/Cvoya.Spring.Web/src/app/` must:

1. Export `generateStaticParams()` returning `[{ <segment>: "__placeholder__" }]`.
2. Delegate rendering to a `"use client"` component that reads the param from `useParams` (or awaits the `params` Promise in the server-component wrapper, as the existing `[id]/page.tsx` files do).
3. Guard the client against the literal `"__placeholder__"` value and render a minimal "no id specified" state for it. Mirrors the pattern in `src/Cvoya.Spring.Web/src/app/agents/[id]/agent-detail-client.tsx` and `src/Cvoya.Spring.Web/src/app/units/[id]/unit-config-client.tsx`.
4. Document the rewrite requirement for the serving layer in the route's PR description.

A lint rule enforcing (1)–(3) is out of scope for this decision; the pattern is small enough to catch in review. If more than ~5 dynamic routes accumulate, promote it to an ESLint custom rule or a pre-commit check.

## Revisit criteria

Reopen this decision if **any** of the following becomes true:

- A feature legitimately needs server-rendered HTML (SEO for public pages, per-request personalization in the shell, streaming large responses, server components that call an internal service with credentials the browser can't hold).
- The portal grows an authenticated landing surface that must render differently per tenant before the SPA hydrates.
- The private repo's hosting model changes such that a Node runtime is already on the path (e.g. Next.js on Vercel / Azure Static Web Apps with functions) and removing static-export friction becomes free.
- The `__placeholder__` guard count exceeds ~5 routes — at that scale the ergonomic cost of the workaround may exceed the deployability benefit.

If the decision is revisited and SSR wins, the migration path is: set `output: "standalone"` in `next.config.ts`, delete the `generateStaticParams` placeholder from every `[id]/page.tsx`, drop the `__placeholder__` guard in each `*-client.tsx`, add a Node runtime to the container image (the Dockerfile already has Node installed), and point the spring-web container at `node server.js` instead of static asset serving. Route-level behavior doesn't change for users.
