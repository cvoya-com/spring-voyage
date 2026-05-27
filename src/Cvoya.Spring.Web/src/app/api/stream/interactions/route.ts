// Next.js route handler that proxies the platform's interactions SSE
// stream (`GET {API}/api/v1/tenant/observation/interactions/stream`) to
// the browser. Same-origin proxy pattern as
// `src/app/api/stream/activity/route.ts` — see that file for the full
// rationale (cookies + EventSource + tenant middleware + abort).
//
// Query params (`unit`, `neighbours`, `coalesceMs`, `maxRate`) are
// passed through verbatim. The `Last-Event-ID` header is forwarded so
// the platform's resume contract is honoured across reconnects.

import type { NextRequest } from "next/server";

export const dynamic = "force-dynamic";
export const runtime = "nodejs";

const UPSTREAM_BASE =
  process.env.SPRING_API_URL ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5000";

export async function GET(request: NextRequest) {
  const url = new URL(request.url);
  const upstreamUrl = new URL(
    "/api/v1/tenant/observation/interactions/stream",
    UPSTREAM_BASE,
  );
  for (const [key, value] of url.searchParams.entries()) {
    upstreamUrl.searchParams.append(key, value);
  }

  const forwardedHeaders: Record<string, string> = {
    accept: "text/event-stream",
  };
  const cookie = request.headers.get("cookie");
  if (cookie) forwardedHeaders.cookie = cookie;
  const auth = request.headers.get("authorization");
  if (auth) forwardedHeaders.authorization = auth;
  // Honour the EventSource reconnect contract — when the browser
  // reconnects after a transient drop it sets `Last-Event-ID` to the
  // last id it saw. The platform endpoint skips any frame whose id is
  // <= that value, so the client never sees duplicates.
  const lastEventId = request.headers.get("last-event-id");
  if (lastEventId) forwardedHeaders["last-event-id"] = lastEventId;

  let upstream: Response;
  try {
    upstream = await fetch(upstreamUrl.toString(), {
      method: "GET",
      headers: forwardedHeaders,
      signal: request.signal,
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

  return new Response(upstream.body, {
    status: 200,
    headers: {
      "content-type": "text/event-stream; charset=utf-8",
      "cache-control": "no-cache, no-transform",
      connection: "keep-alive",
      "x-accel-buffering": "no",
    },
  });
}
