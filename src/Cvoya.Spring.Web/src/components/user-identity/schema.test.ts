import { describe, expect, it } from "vitest";

import {
  parseUserConfigSchema,
  readIdentityField,
  schemaFieldToRequestKey,
} from "./schema";

describe("parseUserConfigSchema", () => {
  it("extracts string-typed properties with required + nullable metadata", () => {
    const schema = {
      type: "object",
      required: ["username"],
      properties: {
        username: { type: "string", description: "GitHub login." },
        display_handle: {
          type: ["string", "null"],
          description: "Optional friendly rendering.",
        },
      },
    };

    expect(parseUserConfigSchema(schema)).toEqual([
      {
        name: "username",
        label: "Username",
        description: "GitHub login.",
        required: true,
        nullable: false,
      },
      {
        name: "display_handle",
        label: "Display Handle",
        description: "Optional friendly rendering.",
        required: false,
        nullable: true,
      },
    ]);
  });

  it("filters out non-string fields (display identity is string-valued in v0.1)", () => {
    const schema = {
      properties: {
        username: { type: "string" },
        max_followers: { type: "integer" },
      },
    };
    const fields = parseUserConfigSchema(schema);
    expect(fields.map((f) => f.name)).toEqual(["username"]);
  });

  it("returns an empty array when the schema is null or malformed", () => {
    expect(parseUserConfigSchema(null)).toEqual([]);
    expect(parseUserConfigSchema({})).toEqual([]);
    expect(parseUserConfigSchema({ properties: 42 })).toEqual([]);
  });
});

describe("schemaFieldToRequestKey", () => {
  it("maps known schema field names to wire request keys", () => {
    expect(schemaFieldToRequestKey("username")).toBe("username");
    expect(schemaFieldToRequestKey("display_handle")).toBe("displayHandle");
  });

  it("returns null for unknown fields", () => {
    expect(schemaFieldToRequestKey("nickname")).toBeNull();
  });
});

describe("readIdentityField", () => {
  const identity = {
    username: "octocat",
    displayHandle: "Octocat (@octocat)",
  };

  it("reads the username column for the matching schema field", () => {
    expect(readIdentityField(identity, "username")).toBe("octocat");
  });

  it("falls back to '' when the displayHandle is null", () => {
    expect(
      readIdentityField({ username: "octocat", displayHandle: null }, "display_handle"),
    ).toBe("");
  });

  it("returns '' when the identity is null", () => {
    expect(readIdentityField(null, "username")).toBe("");
  });
});
