"use client";

import { useState, type ReactNode } from "react";
import { QueryClientProvider } from "@tanstack/react-query";

import { createQueryClient } from "./query-client";

/**
 * Client boundary that mounts a single `QueryClient` for the whole
 * app tree. Placed under the root server layout in `app/layout.tsx`.
 *
 * A new client is created per browser session via `useState` (so the
 * client survives fast refresh and doesn't leak between SSR renders).
 */
export function QueryProvider({ children }: { children: ReactNode }) {
  const [client] = useState(createQueryClient);
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}
