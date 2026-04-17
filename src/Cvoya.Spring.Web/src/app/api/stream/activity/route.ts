// Next.js route handler that proxies the platform's activity stream
// (`GET {API}/api/v1/activity/stream`) to the browser.
//
// The upstream endpoint already emits `text/event-stream` — this
// handler simply forwards the streaming body so the browser talks to
// same-origin `/api/stream/activity`. Same-origin matters for a few
// reasons:
//
//   - `EventSource` doesn't send custom headers; cookies carry auth.
//     Same-origin makes the cookie ride along without CORS gymnastics.
//   - The hosted extension (Spring Voyage Cloud) can layer tenant
//     middleware in its Next.js host before the request hits the
//     platform API — the activity stream participates in that story
//     for free.
//   - Route handlers close the upstream fetch when the client aborts,
//     which matches `ActivityEndpoints.StreamActivityAsync`'s
//     cancellation semantics.
//
// Query filters (`source`, `severity`) are passed through verbatim to
// the platform endpoint.
//
// See docs/architecture/observability.md and
// docs/design/portal-exploration.md §8.3 for the end-to-end story.

import type { NextRequest } from "next/server";

// SSE responses never fit server-side rendering — always run on demand.
export const dynamic = "force-dynamic";
export const runtime = "nodejs";

const UPSTREAM_BASE =
  process.env.SPRING_API_URL ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5000";

export async function GET(request: NextRequest) {
  const url = new URL(request.url);
  const upstreamUrl = new URL("/api/v1/activity/stream", UPSTREAM_BASE);
  // Pass filter params through unchanged.
  for (const [key, value] of url.searchParams.entries()) {
    upstreamUrl.searchParams.append(key, value);
  }

  // Forward auth/cookie headers so the platform's permission filter
  // (see ActivityEndpoints.StreamActivityAsync → IsAuthorizedToObserve)
  // sees the caller identity.
  const forwardedHeaders: Record<string, string> = {
    accept: "text/event-stream",
  };
  const cookie = request.headers.get("cookie");
  if (cookie) forwardedHeaders.cookie = cookie;
  const auth = request.headers.get("authorization");
  if (auth) forwardedHeaders.authorization = auth;

  let upstream: Response;
  try {
    upstream = await fetch(upstreamUrl.toString(), {
      method: "GET",
      headers: forwardedHeaders,
      // Abort the upstream fetch when the client disconnects.
      signal: request.signal,
      // Opt out of Next.js caching (cache: "no-store" is implied when
      // `dynamic = "force-dynamic"`, but be explicit).
      cache: "no-store",
    });
  } catch (err) {
    if ((err as { name?: string })?.name === "AbortError") {
      return new Response(null, { status: 499 });
    }
    return new Response(
      `upstream fetch failed: ${(err as Error).message}`,
      { status: 502 },
    );
  }

  if (!upstream.ok || !upstream.body) {
    return new Response(
      `upstream responded with ${upstream.status} ${upstream.statusText}`,
      { status: upstream.status || 502 },
    );
  }

  // Stream the upstream body straight through. The body is already
  // `text/event-stream` framed — don't buffer, don't transform.
  return new Response(upstream.body, {
    status: 200,
    headers: {
      "content-type": "text/event-stream; charset=utf-8",
      "cache-control": "no-cache, no-transform",
      connection: "keep-alive",
      // `X-Accel-Buffering: no` disables buffering on nginx proxies
      // so events actually flush to the browser in real time.
      "x-accel-buffering": "no",
    },
  });
}
