// Side-effect barrel for the Explorer tab registry.
//
// Each per-tab module registers itself at module top-level via
// `registerTab(...)`. Importing this file once from the Explorer
// route (`src/app/units/page.tsx`) wires every tab into the shared
// registry. Keeping the side-effect imports concentrated here means
// individual tab bundles stay lazy until the Explorer actually loads.

// Unit tabs
import "./unit-overview";
// #2270 / #2427: renamed from `unit-agents` to `unit-members` — the
// composition slot now surfaces agents + sub-units + human team-role
// members in one grid. Hard rename, no `?tab=Agents` shim.
import "./unit-members";
import "./unit-activity";
import "./unit-messages";
import "./unit-memory";
import "./unit-skills"; // #2271
import "./unit-traces"; // #2272
import "./unit-policies";
import "./unit-config";
import "./unit-deployment"; // #2273

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

// Tenant tabs — Memory intentionally absent (#2257; tenant does not
// have memory, see docs/design/canonical-tabs.md § 1 / § 4.1).
// Config is new under #2254 (tenant-default credentials, tenant budget
// editor, tenant cloning-policy summary). The `/settings` page keeps
// its cards — same panel bodies, two access paths.
import "./tenant-overview";
import "./tenant-activity";
import "./tenant-policies";
import "./tenant-budgets";
import "./tenant-config"; // #2254

// Human tabs (#2266 / #2267). Humans are a fourth Explorer subject
// per ADR-0044 and docs/design/canonical-tabs.md § 4.1 — they implement
// only `IMessageReceiver` so the catalog is Overview + Messages
// visible, Config in overflow (no Memory, Agents, Skills, Traces,
// Clones, Policies, Budgets, or Deployment slots — see § 4 matrix).
// Overview (#2267), Messages (#2268), and Config (#2269 — Identity +
// Connector sub-tabs) are all live as of Portal Wave B.
import "./human-overview"; // #2267
import "./human-messages"; // #2268
import "./human-config"; // #2269 — Identity + Connector sub-tabs
