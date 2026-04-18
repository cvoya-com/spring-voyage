import path from "node:path";
import type { NextConfig } from "next";

// Connector packages ship their own web UI under
// `src/Cvoya.Spring.Connector.<Name>/web/` — physically outside this
// Next.js project root. Turbopack's default project-root heuristic
// (walks up from next.config.ts looking for a lockfile) picks this
// directory, and then refuses to resolve `node_modules` for files
// imported from above it. That used to force each connector component
// to live inside `src/Cvoya.Spring.Web/src/connectors/<slug>/` instead
// of its owning package — see #198 for history.
//
// Setting `turbopack.root` to the monorepo root teaches Turbopack that
// the whole repo is one logical project. The web project still hosts
// the `node_modules` that the connector components consume (react,
// lucide-react, @/components/ui/*, etc.); Turbopack walks up from an
// imported file until it finds a matching module, which now succeeds
// because the repo root is an ancestor of both the connector package
// and `node_modules`.
//
// The tsconfig path alias `@connector-github/*` (see
// `src/Cvoya.Spring.Web/tsconfig.json`) resolves to the GitHub connector
// package's `web/` directory, so the registry imports stay symbolic.
const monorepoRoot = path.resolve(__dirname, "..", "..");

const nextConfig: NextConfig = {
  output: "standalone",
  outputFileTracingRoot: monorepoRoot,
  images: { unoptimized: true },
  turbopack: {
    root: monorepoRoot,
  },

  // Legacy path redirects. `/budgets` used to be the budgets editor;
  // PR-S1 Sub-PR A (#544) / PR-S2 (#448) promote it under the new
  // Analytics surface as `/analytics/costs`. A permanent 308 keeps
  // bookmarks and old docs honest.
  async redirects() {
    return [
      {
        source: "/budgets",
        destination: "/analytics/costs",
        permanent: true,
      },
    ];
  },
};

export default nextConfig;
