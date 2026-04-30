// Side-effect barrel for the Explorer tab registry.
//
// Each per-tab module registers itself at module top-level via
// `registerTab(...)`. Importing this file once from the Explorer
// route (`src/app/units/page.tsx`) wires every tab into the shared
// registry. Keeping the side-effect imports concentrated here means
// individual tab bundles stay lazy until the Explorer actually loads.

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
import "./agent-policies";
import "./agent-config";
import "./agent-deployment"; // #1119

// Tenant tabs
import "./tenant-overview";
import "./tenant-activity";
import "./tenant-policies";
import "./tenant-budgets";
import "./tenant-memory";
