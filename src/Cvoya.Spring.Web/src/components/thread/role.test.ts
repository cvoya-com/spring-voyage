import { describe, expect, it } from "vitest";

import {
  addressOf,
  idOf,
  isHumanAddress,
  parseThreadSource,
  participantDisplayName,
  ROLE_STYLES,
  roleFromEvent,
  sameIdentity,
} from "./role";

describe("parseThreadSource", () => {
  it("splits scheme and path for navigation form", () => {
    expect(parseThreadSource("agent://ada")).toEqual({
      scheme: "agent",
      path: "ada",
      kind: "navigation",
      raw: "agent://ada",
    });
  });

  it("preserves nested paths in navigation form", () => {
    expect(parseThreadSource("agent://team/ada")).toEqual({
      scheme: "agent",
      path: "team/ada",
      kind: "navigation",
      raw: "agent://team/ada",
    });
  });

  it("falls back to system scheme when no separator is present", () => {
    expect(parseThreadSource("scheduler")).toEqual({
      scheme: "system",
      path: "scheduler",
      kind: "navigation",
      raw: "scheduler",
    });
  });

  it("lowercases the scheme for navigation form", () => {
    expect(parseThreadSource("HUMAN://savas")).toEqual({
      scheme: "human",
      path: "savas",
      kind: "navigation",
      raw: "HUMAN://savas",
    });
  });

  // Identity form tests (#1490)
  it("parses identity form agent:id:<uuid>", () => {
    const uuid = "1f9e3c2d-0000-0000-0000-000000000001";
    expect(parseThreadSource(`agent:id:${uuid}`)).toEqual({
      scheme: "agent",
      path: uuid,
      kind: "identity",
      raw: `agent:id:${uuid}`,
    });
  });

  it("parses identity form unit:id:<uuid>", () => {
    const uuid = "2a3b4c5d-0000-0000-0000-000000000002";
    expect(parseThreadSource(`unit:id:${uuid}`)).toEqual({
      scheme: "unit",
      path: uuid,
      kind: "identity",
      raw: `unit:id:${uuid}`,
    });
  });

  it("lowercases the scheme for identity form", () => {
    const uuid = "1f9e3c2d-0000-0000-0000-000000000001";
    const result = parseThreadSource(`AGENT:id:${uuid}`);
    expect(result.scheme).toBe("agent");
    expect(result.kind).toBe("identity");
  });

  it("falls back to navigation parse when ':id:' path contains slashes (not a UUID)", () => {
    // "agent:id:some/path" does not look like an identity form — the path
    // has slashes, so fall through to nav-form parsing.
    const result = parseThreadSource("agent://id:some/path");
    expect(result.kind).toBe("navigation");
    expect(result.scheme).toBe("agent");
  });
});

describe("isHumanAddress", () => {
  it("returns true for human:// navigation form", () => {
    expect(isHumanAddress("human://savas")).toBe(true);
  });

  it("returns false for agent:id: identity form", () => {
    expect(isHumanAddress("agent:id:1f9e3c2d-0000-0000-0000-000000000001")).toBe(false);
  });

  it("returns false for unit:// navigation form", () => {
    expect(isHumanAddress("unit://engineering")).toBe(false);
  });
});

describe("roleFromEvent", () => {
  it("maps navigation-form scheme to role for message events", () => {
    expect(roleFromEvent("human://savas", "MessageReceived")).toBe("human");
    expect(roleFromEvent("agent://ada", "MessageSent")).toBe("agent");
    expect(roleFromEvent("unit://eng", "ConversationStarted")).toBe("unit");
  });

  it("maps identity-form scheme to role for message events (#1490)", () => {
    const uuid = "1f9e3c2d-0000-0000-0000-000000000001";
    expect(roleFromEvent(`agent:id:${uuid}`, "MessageReceived")).toBe("agent");
    expect(roleFromEvent(`unit:id:${uuid}`, "MessageReceived")).toBe("unit");
  });

  it("treats DecisionMade as a tool call regardless of source form", () => {
    const uuid = "1f9e3c2d-0000-0000-0000-000000000001";
    expect(roleFromEvent("agent://ada", "DecisionMade")).toBe("tool");
    expect(roleFromEvent(`agent:id:${uuid}`, "DecisionMade")).toBe("tool");
    expect(roleFromEvent("unit://eng", "DecisionMade")).toBe("tool");
  });

  it("falls back to system for unknown schemes", () => {
    expect(roleFromEvent("scheduler", "StateChanged")).toBe("system");
    expect(roleFromEvent("foo://bar", "StateChanged")).toBe("system");
  });
});

describe("ROLE_STYLES", () => {
  it("right-aligns human bubbles and left-aligns the rest", () => {
    expect(ROLE_STYLES.human.align).toBe("end");
    expect(ROLE_STYLES.agent.align).toBe("start");
    expect(ROLE_STYLES.unit.align).toBe("start");
    expect(ROLE_STYLES.tool.align).toBe("start");
    expect(ROLE_STYLES.system.align).toBe("start");
  });
});

// #1630
describe("addressOf", () => {
  it("returns the address from a ParticipantRef object", () => {
    expect(addressOf({ address: "agent://ada", displayName: "ada" })).toBe(
      "agent://ada",
    );
  });

  it("returns plain string addresses unchanged", () => {
    expect(addressOf("agent://ada")).toBe("agent://ada");
  });

  it("returns empty string for null / undefined", () => {
    expect(addressOf(null)).toBe("");
    expect(addressOf(undefined)).toBe("");
  });

  it("returns empty string when address field is missing", () => {
    expect(addressOf({ displayName: "ada" })).toBe("");
  });
});

// #2082 — identity equality on the typed Guid id, not on address strings.
describe("idOf", () => {
  it("returns the lowercased id when present on a ParticipantRef", () => {
    expect(
      idOf({
        id: "ABCDEF01-1234-5678-9ABC-DEF012345678",
        address: "agent:abcdef0112345678abcd",
        displayName: "ada",
      }),
    ).toBe("abcdef01-1234-5678-9abc-def012345678");
  });

  it("returns null for plain string participants (no typed id)", () => {
    expect(idOf("agent://ada")).toBeNull();
  });

  it("returns null when the id field is empty / missing", () => {
    expect(idOf({ address: "agent://ada", displayName: "ada" })).toBeNull();
    expect(
      idOf({ id: "", address: "agent://ada", displayName: "ada" }),
    ).toBeNull();
  });

  it("returns null for null / undefined", () => {
    expect(idOf(null)).toBeNull();
    expect(idOf(undefined)).toBeNull();
  });
});

describe("sameIdentity", () => {
  it("compares on id when both sides have one", () => {
    const a = {
      id: "11111111-1111-1111-1111-111111111111",
      address: "human://savas",
      displayName: "savas",
    };
    const b = {
      id: "11111111-1111-1111-1111-111111111111",
      address: "human:11111111111111111111111111111111",
      displayName: "savas",
    };
    expect(sameIdentity(a, b)).toBe(true);
  });

  it("returns false when ids differ even if addresses match", () => {
    expect(
      sameIdentity(
        {
          id: "11111111-1111-1111-1111-111111111111",
          address: "human://savas",
          displayName: "savas",
        },
        {
          id: "22222222-2222-2222-2222-222222222222",
          address: "human://savas",
          displayName: "savas",
        },
      ),
    ).toBe(false);
  });

  it("returns false when either side has no id", () => {
    expect(
      sameIdentity(
        {
          id: "11111111-1111-1111-1111-111111111111",
          address: "human://savas",
          displayName: "savas",
        },
        { address: "human://savas", displayName: "savas" },
      ),
    ).toBe(false);
    expect(sameIdentity("human://savas", "human://savas")).toBe(false);
  });
});

// #1635 / #1645 — post-#1635 (PR #1643) the server guarantees a
// non-empty `displayName` on every ParticipantRef-shaped DTO. The portal
// is a thin pass-through over the server-supplied label; if a raw GUID
// leaks into the UI, that's a server-side resolver bug, not something
// the portal masks (#1645 removed the legacy `looksLikeUuid` heuristic).
describe("participantDisplayName", () => {
  it("returns the server-supplied displayName when present", () => {
    expect(
      participantDisplayName({
        address: "agent://ada",
        displayName: "Ada Lovelace",
      }),
    ).toBe("Ada Lovelace");
  });

  it("trims surrounding whitespace on the server-supplied displayName", () => {
    expect(
      participantDisplayName({
        address: "agent://ada",
        displayName: "  Ada Lovelace  ",
      }),
    ).toBe("Ada Lovelace");
  });

  it("returns the deleted-sentinel pass-through when the server emits one", () => {
    // The server resolver returns "<deleted>" for entities the
    // directory can no longer resolve (#1635). The portal renders that
    // string directly.
    expect(
      participantDisplayName({
        address: "agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7",
        displayName: "<deleted>",
      }),
    ).toBe("<deleted>");
  });

  it("returns null when a ParticipantRef has an empty displayName", () => {
    // Caller surfaces its own "Unknown participant" fallback in this
    // case; the portal does not synthesise a name from the address.
    expect(participantDisplayName({ address: "agent://ada" })).toBeNull();
    expect(
      participantDisplayName({ address: "agent://ada", displayName: "" }),
    ).toBeNull();
    expect(
      participantDisplayName({ address: "agent://ada", displayName: "   " }),
    ).toBeNull();
  });

  it("returns null for null / undefined / bare-string / empty inputs", () => {
    expect(participantDisplayName(null)).toBeNull();
    expect(participantDisplayName(undefined)).toBeNull();
    expect(participantDisplayName("")).toBeNull();
    // Bare-string inputs (pre-#1502 server shape) no longer round-trip
    // through a navigation-form heuristic — the portal expects a
    // ParticipantRef with a server-resolved displayName.
    expect(participantDisplayName("agent://ada")).toBeNull();
  });
});
