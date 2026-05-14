#!/usr/bin/env node
// Block pnpm/yarn from installing in this repo. The workspace is managed
// by npm (root package-lock.json + npm "workspaces" field). Running pnpm
// here once already corrupted node_modules with absolute-path shims that
// broke the Docker build (eng/build/Dockerfile -> npm run build).

const ua = process.env.npm_config_user_agent || "";
const tool = ua.split("/")[0] || "unknown";

if (tool !== "npm") {
  console.error(
    `\n[error] This repository uses npm. Detected: ${tool}.\n` +
      `        Use \`npm install\` or \`npm ci\` instead.\n`,
  );
  process.exit(1);
}
