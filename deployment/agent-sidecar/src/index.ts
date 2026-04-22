// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Library entry point. The CLI lives in cli.ts. Importing from
// "@cvoya/spring-voyage-agent-sidecar" gives you the building blocks
// for embedding the bridge in a custom Node entrypoint (BYOI path 2).

export { runAgentBridge, type BridgeRunOptions, type BridgeRunResult } from "./bridge.js";
export { A2AHandler, type AgentCard, type JsonRpcRequest, type JsonRpcResponse, type TaskState } from "./a2a.js";
export { createServer, type SidecarServer } from "./server.js";
export { loadConfigFromEnv, type BridgeConfig } from "./config.js";
export { A2A_PROTOCOL_VERSION, BRIDGE_VERSION } from "./version.js";
