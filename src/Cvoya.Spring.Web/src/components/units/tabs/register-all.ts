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
