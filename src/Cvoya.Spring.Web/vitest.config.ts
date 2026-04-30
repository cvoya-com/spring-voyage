import { createRequire } from "module";
import { defineConfig } from "vitest/config";
import { resolve } from "path";

// Resolve React to the copy that this package (spring-voyage-dashboard)
// actually installs. Using createRequire(__filename) to resolve from this
// config file's own directory guarantees we pick up the same physical React
// that @testing-library/react and React DOM use — not a hoisted workspace-
// root copy that might be a different module instance. Two distinct React
// instances cause "Invalid hook call" / "Cannot read properties of null
// (reading 'useState')" when connector components (imported via the
// @connector-github alias) call hooks. Pinning both react and react-dom to
// the same location enforces a single instance for the entire test run.
// See #1384.
const require = createRequire(import.meta.url);
const reactDir = resolve(require.resolve("react"), "..");
const reactDomDir = resolve(require.resolve("react-dom"), "..");

export default defineConfig({
  // Setting jsx: "automatic" matches the tsconfig "jsx: react-jsx"
  // setting so connector-package TSX files (imported via the
  // @connector-* aliases below) compile under Vitest's esbuild loader.
  // Without this, those files fail with "React is not defined" because
  // esbuild falls back to the classic runtime for files outside the
  // web project's own tsconfig include list.
  esbuild: {
    jsx: "automatic",
  },
  resolve: {
    alias: {
      "@": resolve(__dirname, "./src"),
      "@connectors": resolve(__dirname, "./src/connectors"),
      // Connector-package aliases must stay in sync with tsconfig.json.
      // The web workspace lives at src/Cvoya.Spring.Web; each connector
      // package sits as a sibling under src/Cvoya.Spring.Connector.<Name>/.
      "@connector-github": resolve(
        __dirname,
        "../Cvoya.Spring.Connector.GitHub/web",
      ),
      // Pin react/react-dom to the instance resolved above (see comment
      // at top of file). Without this, files outside the web project
      // (connector packages) pick up a different React copy, causing the
      // duplicate-instance hook error. See #1384.
      react: reactDir,
      "react-dom": reactDomDir,
    },
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    // Playwright specs in e2e/ have their own runner — keep vitest from
    // discovering them (its default include glob would otherwise pick up
    // anything under the project that ends in .test.ts(x), but we also
    // want to exclude any future *.spec.ts files which are Playwright
    // convention).
    exclude: ["node_modules/**", ".next/**", "e2e/**"],
    coverage: {
      provider: "v8",
      reporter: ["text-summary", "lcov", "html"],
      reportsDirectory: "./coverage",
      // Match the same source roots vitest discovers tests for. We exclude
      // generated/contract files (openapi-typescript output, Next.js's
      // auto-generated `.next/types`), the test setup itself, and pure
      // markup-only registry/exports files where coverage is meaningless.
      include: ["src/**/*.{ts,tsx}"],
      exclude: [
        "src/lib/api/schema.d.ts",
        "src/test/**",
        "src/**/*.test.{ts,tsx}",
        "src/**/*.d.ts",
        "src/connectors/registry.ts",
        ".next/**",
      ],
      // Floor — chosen below the current measured baseline (lines ≈ 70%,
      // functions ≈ 56%, branches ≈ 78%, statements ≈ 70% as of 2026-04-21)
      // so coverage erosion fails CI. Ratchet upward as the suite grows.
      thresholds: {
        lines: 65,
        functions: 50,
        branches: 70,
        statements: 65,
      },
    },
  },
});
