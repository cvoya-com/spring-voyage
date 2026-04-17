/**
 * Tests for the `/api/stream/activity` SSE route handler. The handler
 * forwards a fetch to the platform's `/api/v1/activity/stream` and
 * pipes the upstream body straight back. These tests mock `fetch`
 * to assert:
 *
 *   - content-type headers indicate `text/event-stream`
 *   - query params get forwarded to the upstream URL
 *   - the body streamed by the upstream is reachable from the response
 *   - upstream failures surface as a 5xx
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { GET } from "./route";

type FetchArgs = Parameters<typeof fetch>;

function makeRequest(url = "http://localhost/api/stream/activity") {
  return new Request(url, { method: "GET" });
}

describe("GET /api/stream/activity", () => {
  const originalFetch = globalThis.fetch;

  beforeEach(() => {
    // reset fetch before every test
    globalThis.fetch = originalFetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it("proxies the upstream body and sets SSE headers", async () => {
    const upstreamBody = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(
          new TextEncoder().encode('data: {"id":"1"}\n\n'),
        );
        controller.close();
      },
    });
    const fetchMock = vi.fn(
      async (..._args: FetchArgs) =>
        new Response(upstreamBody, {
          status: 200,
          headers: { "content-type": "text/event-stream" },
        }),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const response = await GET(makeRequest() as never);

    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toMatch(/text\/event-stream/);
    expect(response.headers.get("cache-control")).toMatch(/no-cache/);

    const text = await response.text();
    expect(text).toContain('data: {"id":"1"}');
  });

  it("forwards query parameters to the upstream URL", async () => {
    let observedUrl: string | null = null;
    globalThis.fetch = (async (...args: FetchArgs) => {
      observedUrl = String(args[0]);
      return new Response(new ReadableStream<Uint8Array>(), {
        status: 200,
        headers: { "content-type": "text/event-stream" },
      });
    }) as unknown as typeof fetch;

    await GET(
      makeRequest(
        "http://localhost/api/stream/activity?source=unit://eng&severity=Warning",
      ) as never,
    );

    expect(observedUrl).not.toBeNull();
    const u = new URL(observedUrl!);
    expect(u.pathname).toBe("/api/v1/activity/stream");
    expect(u.searchParams.get("source")).toBe("unit://eng");
    expect(u.searchParams.get("severity")).toBe("Warning");
  });

  it("returns the upstream status code on non-2xx", async () => {
    globalThis.fetch = (async () =>
      new Response("unavailable", {
        status: 503,
        statusText: "Service Unavailable",
      })) as unknown as typeof fetch;

    const response = await GET(makeRequest() as never);
    expect(response.status).toBe(503);
  });

  it("returns 502 when fetch itself throws", async () => {
    globalThis.fetch = (async () => {
      throw new Error("connection refused");
    }) as unknown as typeof fetch;

    const response = await GET(makeRequest() as never);
    expect(response.status).toBe(502);
    const text = await response.text();
    expect(text).toContain("upstream fetch failed");
  });
});
