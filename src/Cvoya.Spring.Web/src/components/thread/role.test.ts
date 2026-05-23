import { describe, expect, it } from "vitest";

import {
  addressOf,
  buildParticipantNameResolver,
  idOf,
  isHumanAddress,
  parseThreadSource,
  participantDisplayName,
  renderBodyWithResolvedAddresses,
  ROLE_STYLES,
  roleFromEvent,
  sameIdentity,
  splitBodyIntoStructuredAndProse,
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
    expect(roleFromEvent("human://savas", "MessageArrived")).toBe("human");
    expect(roleFromEvent("agent://ada", "MessageSent")).toBe("agent");
    expect(roleFromEvent("unit://eng", "ConversationStarted")).toBe("unit");
  });

  it("maps identity-form scheme to role for message events (#1490)", () => {
    const uuid = "1f9e3c2d-0000-0000-0000-000000000001";
    expect(roleFromEvent(`agent:id:${uuid}`, "MessageArrived")).toBe("agent");
    expect(roleFromEvent(`unit:id:${uuid}`, "MessageArrived")).toBe("unit");
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

// #2089 — body-text address-folding. A weak / noisy LLM may mimic the
// prompt-format the agent SDK uses for prior turns and emit
// `[ts] human://<guid>: …` inside its own reply body. The portal folds
// every `scheme:[//]<guid>` form down to the participant's display
// name so platform-internal addressing doesn't leak into the chat UI.
describe("buildParticipantNameResolver + renderBodyWithResolvedAddresses", () => {
  const SAVAS = {
    id: "d6cb6b9d-436f-41d5-9927-f333f309abeb",
    address: "human:d6cb6b9d436f41d59927f333f309abeb",
    displayName: "Savas",
  };
  const ADA = {
    id: "8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7",
    address: "agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7",
    displayName: "ada",
  };

  it("folds the canonical post-#1629 `scheme://<32-hex>` form", () => {
    const resolve = buildParticipantNameResolver([SAVAS]);
    expect(
      renderBodyWithResolvedAddresses(
        "human://d6cb6b9d436f41d59927f333f309abeb: hi",
        resolve,
      ),
    ).toBe("Savas: hi");
  });

  it("folds the colon-only canonical form `scheme:<32-hex>`", () => {
    const resolve = buildParticipantNameResolver([SAVAS]);
    expect(
      renderBodyWithResolvedAddresses(
        "ping human:d6cb6b9d436f41d59927f333f309abeb",
        resolve,
      ),
    ).toBe("ping Savas");
  });

  it("folds the dashed-uuid path form", () => {
    const resolve = buildParticipantNameResolver([SAVAS]);
    expect(
      renderBodyWithResolvedAddresses(
        "human://d6cb6b9d-436f-41d5-9927-f333f309abeb says hi",
        resolve,
      ),
    ).toBe("Savas says hi");
  });

  it("folds the explicit `scheme:id:<dashed-uuid>` identity form", () => {
    const resolve = buildParticipantNameResolver([SAVAS]);
    expect(
      renderBodyWithResolvedAddresses(
        "ack human:id:d6cb6b9d-436f-41d5-9927-f333f309abeb done",
        resolve,
      ),
    ).toBe("ack Savas done");
  });

  it("folds multiple addresses in the same body", () => {
    const resolve = buildParticipantNameResolver([SAVAS, ADA]);
    expect(
      renderBodyWithResolvedAddresses(
        "human://d6cb6b9d436f41d59927f333f309abeb to agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7",
        resolve,
      ),
    ).toBe("Savas to ada");
  });

  it("preserves surrounding prose and whitespace verbatim", () => {
    const resolve = buildParticipantNameResolver([SAVAS]);
    const before = `[2026-05-10 20:54:39Z] human://d6cb6b9d436f41d59927f333f309abeb: can you check?`;
    expect(renderBodyWithResolvedAddresses(before, resolve)).toBe(
      "[2026-05-10 20:54:39Z] Savas: can you check?",
    );
  });

  it("renders <unknown> when the address cannot be resolved", () => {
    const resolve = buildParticipantNameResolver([]);
    expect(
      renderBodyWithResolvedAddresses(
        "human://d6cb6b9d436f41d59927f333f309abeb",
        resolve,
      ),
    ).toBe("<unknown>");
  });

  it("passes through the server-supplied <deleted> sentinel", () => {
    const resolve = buildParticipantNameResolver([
      {
        id: "d6cb6b9d-436f-41d5-9927-f333f309abeb",
        address: "human:d6cb6b9d436f41d59927f333f309abeb",
        displayName: "<deleted>",
      },
    ]);
    expect(
      renderBodyWithResolvedAddresses(
        "human://d6cb6b9d436f41d59927f333f309abeb left",
        resolve,
      ),
    ).toBe("<deleted> left");
  });

  it("is idempotent over already-rendered bodies", () => {
    const resolve = buildParticipantNameResolver([SAVAS]);
    const once = renderBodyWithResolvedAddresses(
      "human://d6cb6b9d436f41d59927f333f309abeb",
      resolve,
    );
    expect(renderBodyWithResolvedAddresses(once, resolve)).toBe(once);
  });

  it("ignores tokens that look like addresses but have a wrong-length path", () => {
    const resolve = buildParticipantNameResolver([SAVAS]);
    // not 32-hex and not a dashed UUID — should not be touched.
    expect(
      renderBodyWithResolvedAddresses(
        "see http://human:abcd or human:notaguid",
        resolve,
      ),
    ).toBe("see http://human:abcd or human:notaguid");
  });

  it("supports unit:// and tenant:// scheme prefixes", () => {
    const unit = {
      id: "11111111-2222-3333-4444-555555555555",
      address: "unit:11111111222233334444555555555555",
      displayName: "engineering",
    };
    const tenant = {
      id: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
      address: "tenant:aaaaaaaabbbbccccddddeeeeeeeeeeee",
      displayName: "Acme",
    };
    const resolve = buildParticipantNameResolver([unit, tenant]);
    expect(
      renderBodyWithResolvedAddresses(
        "owner unit://11111111222233334444555555555555 in tenant://aaaaaaaabbbbccccddddeeeeeeeeeeee",
        resolve,
      ),
    ).toBe("owner engineering in Acme");
  });

  it("returns the body unchanged when the resolver finds no addresses", () => {
    const resolve = buildParticipantNameResolver([SAVAS]);
    expect(renderBodyWithResolvedAddresses("just prose", resolve)).toBe(
      "just prose",
    );
  });

  it("returns an empty string for an empty body", () => {
    const resolve = buildParticipantNameResolver([]);
    expect(renderBodyWithResolvedAddresses("", resolve)).toBe("");
  });

  it("skips participants without an id (legacy plain-string entries)", () => {
    // Plain-string participants have no stable id; the resolver cannot
    // key them, so the address falls through to the <unknown> sentinel.
    const resolve = buildParticipantNameResolver([
      "human://d6cb6b9d436f41d59927f333f309abeb",
    ]);
    expect(
      renderBodyWithResolvedAddresses(
        "human://d6cb6b9d436f41d59927f333f309abeb",
        resolve,
      ),
    ).toBe("<unknown>");
  });
});

// #2128: shape-only detection of a leading JSON envelope inside a body.
// Pure helper, callable in isolation by anything that needs the same
// split (the row, future tests, future surfaces). The helper does not
// validate the payload against any tool-result schema — it is a render
// affordance, not a semantic claim.
describe("splitBodyIntoStructuredAndProse", () => {
  it("returns null structured + raw prose when the body is empty", () => {
    expect(splitBodyIntoStructuredAndProse("")).toEqual({
      structured: null,
      prose: "",
    });
  });

  it("returns null structured + raw prose for a body that doesn't start with `{`", () => {
    expect(splitBodyIntoStructuredAndProse("hello there")).toEqual({
      structured: null,
      prose: "hello there",
    });
  });

  it("splits a body whose first non-whitespace token is a parseable JSON object", () => {
    const body = '{"a":1} hello';
    expect(splitBodyIntoStructuredAndProse(body)).toEqual({
      structured: { a: 1 },
      prose: "hello",
    });
  });

  it("tolerates leading whitespace before the JSON envelope", () => {
    const body = '   \n{"a":1}\n\nrest';
    expect(splitBodyIntoStructuredAndProse(body)).toEqual({
      structured: { a: 1 },
      prose: "rest",
    });
  });

  it("returns the parsed object and an empty prose when the body is pure JSON", () => {
    const body = '{"a":1, "b":[1,2,3]}';
    expect(splitBodyIntoStructuredAndProse(body)).toEqual({
      structured: { a: 1, b: [1, 2, 3] },
      prose: "",
    });
  });

  it("returns null structured when the leading `{` doesn't parse", () => {
    const body = "{not valid json}";
    expect(splitBodyIntoStructuredAndProse(body)).toEqual({
      structured: null,
      prose: body,
    });
  });

  it("returns null structured for a leading JSON array (not an object)", () => {
    // Arrays are far less likely to be tool envelopes; we only fold
    // top-level objects to keep the heuristic conservative.
    const body = '[1,2,3] reply';
    expect(splitBodyIntoStructuredAndProse(body)).toEqual({
      structured: null,
      prose: body,
    });
  });

  it("respects strings that contain `}` characters when scanning the envelope", () => {
    // Without proper string-aware scanning the inner `}` would close the
    // top-level object early and JSON.parse would fail on the truncated
    // slice — a regression vector for any body that includes braces in
    // string values.
    const body = '{"x":"a}b","y":1} prose';
    expect(splitBodyIntoStructuredAndProse(body)).toEqual({
      structured: { x: "a}b", y: 1 },
      prose: "prose",
    });
  });

  it("respects nested objects", () => {
    const body = '{"outer":{"inner":{"deep":true}}} done';
    expect(splitBodyIntoStructuredAndProse(body)).toEqual({
      structured: { outer: { inner: { deep: true } } },
      prose: "done",
    });
  });

  it("is idempotent — feeding the prose half back returns the same prose", () => {
    const body = '{"a":1} hello there';
    const first = splitBodyIntoStructuredAndProse(body);
    const second = splitBodyIntoStructuredAndProse(first.prose);
    expect(second).toEqual({ structured: null, prose: "hello there" });
  });

  it("returns null structured when a leading `{` is never closed", () => {
    const body = '{"a":1';
    expect(splitBodyIntoStructuredAndProse(body)).toEqual({
      structured: null,
      prose: body,
    });
  });
});
