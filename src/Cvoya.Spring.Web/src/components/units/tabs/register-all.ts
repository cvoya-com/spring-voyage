// Side-effect barrel for the Explorer tab registry.
//
// Each per-tab module registers itself at module top-level via
// `registerTab(...)`. Importing this file once from the Explorer
// route (`src/app/units/page.tsx`) wires every v2 tab into the
// shared registry. Keeping the side-effect imports concentrated
// here means individual tab bundles stay lazy until the Explorer
// actually loads.
//
// Umbrella: #815. Per-tab issues: EXP-tab-unit-*, EXP-tab-agent-*,
// EXP-tab-tenant.

// Unit tabs
import "./unit-overview";
import "./unit-agents";
import "./unit-orchestration";
import "./unit-activity";
import "./unit-messages";
import "./unit-memory";
import "./unit-policies";
import "./unit-config";

// Agent tabs
import "./agent-overview";
import "./agent-activity";
import "./agent-messages";
import "./agent-memory";
import "./agent-skills";
import "./agent-traces";
import "./agent-clones";
import "./agent-config";

// Tenant tabs
import "./tenant-overview";
import "./tenant-activity";
import "./tenant-policies";
import "./tenant-budgets";
import "./tenant-memory";
