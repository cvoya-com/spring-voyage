// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// PR 3a of #1087 — keep the bridge version in one place so both the Agent
// Card (`/.well-known/agent.json`) and the `x-spring-voyage-bridge-version`
// response header report the same value. The dispatcher logs version skew
// so an operator can correlate odd behaviour with a stale sidecar.
export const BRIDGE_VERSION = "1.0.0";

// A2A protocol version this bridge implements. Pinned to 0.3.x per the
// #1087 plan; bumping this is a deliberate breaking change that requires
// a deprecation window on the dispatcher side.
export const A2A_PROTOCOL_VERSION = "0.3";
