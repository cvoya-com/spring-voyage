import { describe, expect, it } from "vitest";

import {
  AGENT_NAME_PATTERN,
  buildAgentDefinitionJson,
  buildCreateAgentRequest,
  describeAgentCreateError,
  validateAgentCreateInput,
} from "./create-agent";

describe("create-agent helper (#1040)", () => {
  describe("validateAgentCreateInput", () => {
    it("requires a display name", () => {
      expect(
        validateAgentCreateInput({ displayName: " ", unitIds: ["u"] }),
      ).toBe("displayName-required");
    });

    it("requires at least one non-empty unit", () => {
      expect(
        validateAgentCreateInput({
          displayName: "Ada",
          unitIds: ["", "  "],
        }),
      ).toBe("unit-required");
    });

    it("returns null for a well-formed input", () => {
      expect(
        validateAgentCreateInput({
          displayName: "Ada",
          unitIds: ["engineering"],
        }),
      ).toBeNull();
    });
  });

  describe("AGENT_NAME_PATTERN", () => {
    it("accepts lowercase alphanumeric and hyphens", () => {
      expect(AGENT_NAME_PATTERN.test("ada-the-2nd")).toBe(true);
    });

    it("rejects uppercase / underscores / spaces", () => {
      expect(AGENT_NAME_PATTERN.test("Ada")).toBe(false);
      expect(AGENT_NAME_PATTERN.test("ada_2nd")).toBe(false);
      expect(AGENT_NAME_PATTERN.test("ada the 2nd")).toBe(false);
    });
  });

  describe("buildAgentDefinitionJson", () => {
    it("returns null when no execution shorthand was supplied", () => {
      expect(buildAgentDefinitionJson({})).toBeNull();
      expect(
        buildAgentDefinitionJson({
          image: "  ",
          runtime: "",
          tool: undefined,
          model: undefined,
        }),
      ).toBeNull();
    });

    it("returns null when the structured model is supplied with both halves blank", () => {
      expect(
        buildAgentDefinitionJson({
          model: { provider: "  ", id: "" },
        }),
      ).toBeNull();
    });

    it("packs supplied fields under an `execution` block with structured model (ADR-0038 / ADR-0039 I1)", () => {
      const json = buildAgentDefinitionJson({
        image: "ghcr.io/example:latest",
        runtime: "docker",
        tool: "claude-code",
        model: { provider: "anthropic", id: "claude-sonnet-4-6" },
      });
      expect(json).not.toBeNull();
      const parsed = JSON.parse(json as string);
      expect(parsed).toEqual({
        execution: {
          image: "ghcr.io/example:latest",
          runtime: "docker",
          tool: "claude-code",
          model: { provider: "anthropic", id: "claude-sonnet-4-6" },
        },
      });
    });

    it("only includes fields that were actually supplied", () => {
      const json = buildAgentDefinitionJson({ tool: "codex" });
      expect(JSON.parse(json as string)).toEqual({
        execution: { tool: "codex" },
      });
    });

    it("emits only the supplied half of the structured model", () => {
      expect(
        JSON.parse(
          buildAgentDefinitionJson({
            model: { provider: "ollama", id: "" },
          }) as string,
        ),
      ).toEqual({ execution: { model: { provider: "ollama" } } });

      expect(
        JSON.parse(
          buildAgentDefinitionJson({
            model: { provider: "", id: "llama3.2:3b" },
          }) as string,
        ),
      ).toEqual({ execution: { model: { id: "llama3.2:3b" } } });
    });

    it("trims whitespace on provider and id", () => {
      const json = buildAgentDefinitionJson({
        model: { provider: "  anthropic  ", id: "  claude-opus-4-7  " },
      });
      expect(JSON.parse(json as string)).toEqual({
        execution: {
          model: { provider: "anthropic", id: "claude-opus-4-7" },
        },
      });
    });

    it("emits execution.hosting === 'ephemeral' when hosting is 'ephemeral' (ADR-0039 I2)", () => {
      const json = buildAgentDefinitionJson({ hosting: "ephemeral" });
      const parsed = JSON.parse(json as string);
      expect(parsed.execution.hosting).toBe("ephemeral");
    });

    it("emits execution.hosting === 'persistent' when hosting is 'persistent' (ADR-0039 I2)", () => {
      const json = buildAgentDefinitionJson({ hosting: "persistent" });
      const parsed = JSON.parse(json as string);
      expect(parsed.execution.hosting).toBe("persistent");
    });

    it("omits hosting from the serialised JSON when hosting is null — inherit from parent (ADR-0039 I2)", () => {
      // Combine with another field so the function returns a non-null
      // document; we want to verify the *absence* of the hosting key on
      // an otherwise-populated execution block.
      const json = buildAgentDefinitionJson({
        tool: "claude-code",
        hosting: null,
      });
      const parsed = JSON.parse(json as string);
      expect("hosting" in parsed.execution).toBe(false);
    });

    it("omits hosting from the serialised JSON when hosting is undefined", () => {
      const json = buildAgentDefinitionJson({ tool: "claude-code" });
      const parsed = JSON.parse(json as string);
      expect("hosting" in parsed.execution).toBe(false);
    });

    it("returns null when only hosting is null and no other field was supplied", () => {
      expect(buildAgentDefinitionJson({ hosting: null })).toBeNull();
    });

    it("packs hosting alongside other execution fields", () => {
      const json = buildAgentDefinitionJson({
        image: "ghcr.io/example:latest",
        runtime: "docker",
        tool: "claude-code",
        model: { provider: "anthropic", id: "claude-sonnet-4-6" },
        hosting: "persistent",
      });
      expect(JSON.parse(json as string)).toEqual({
        execution: {
          image: "ghcr.io/example:latest",
          runtime: "docker",
          tool: "claude-code",
          model: { provider: "anthropic", id: "claude-sonnet-4-6" },
          hosting: "persistent",
        },
      });
    });
  });

  describe("buildCreateAgentRequest", () => {
    it("trims whitespace and omits definitionJson when no exec shorthand was set", () => {
      const body = buildCreateAgentRequest({
        displayName: "  Ada  ",
        role: "  reviewer  ",
        unitIds: ["  engineering  ", " marketing "],
      });
      expect(body).toEqual({
        displayName: "Ada",
        description: "",
        role: "reviewer",
        unitIds: ["engineering", "marketing"],
      });
      expect("definitionJson" in body).toBe(false);
      expect("name" in body).toBe(false);
    });

    it("normalises an empty role to null", () => {
      const body = buildCreateAgentRequest({
        displayName: "Ada",
        role: "   ",
        unitIds: ["engineering"],
      });
      expect(body.role).toBeNull();
    });

    it("includes definitionJson when execution shorthand is provided", () => {
      const body = buildCreateAgentRequest({
        displayName: "Ada",
        unitIds: ["engineering"],
        tool: "claude-code",
        model: { provider: "anthropic", id: "claude-sonnet-4-6" },
      });
      expect(body.definitionJson).toBe(
        JSON.stringify({
          execution: {
            tool: "claude-code",
            model: { provider: "anthropic", id: "claude-sonnet-4-6" },
          },
        }),
      );
    });

    it("forwards hosting into definitionJson when supplied (ADR-0039 I2)", () => {
      const body = buildCreateAgentRequest({
        displayName: "Ada",
        unitIds: ["engineering"],
        tool: "claude-code",
        hosting: "ephemeral",
      });
      expect(body.definitionJson).toBe(
        JSON.stringify({
          execution: {
            tool: "claude-code",
            hosting: "ephemeral",
          },
        }),
      );
    });

    it("omits hosting from definitionJson when hosting is null (inherit) but other fields are set", () => {
      const body = buildCreateAgentRequest({
        displayName: "Ada",
        unitIds: ["engineering"],
        tool: "claude-code",
        hosting: null,
      });
      expect(body.definitionJson).toBe(
        JSON.stringify({ execution: { tool: "claude-code" } }),
      );
    });

    it("throws on invalid input so the caller can surface the message", () => {
      expect(() =>
        buildCreateAgentRequest({
          displayName: "",
          unitIds: ["engineering"],
        }),
      ).toThrow("Display name is required.");
    });

    it("throws when no unit is supplied", () => {
      expect(() =>
        buildCreateAgentRequest({
          displayName: "Ada",
          unitIds: [],
        }),
      ).toThrow("Pick at least one unit to assign the agent to.");
    });
  });

  describe("describeAgentCreateError", () => {
    it("returns copy for all defined error codes", () => {
      expect(describeAgentCreateError("displayName-required")).toBeTruthy();
      expect(describeAgentCreateError("unit-required")).toBeTruthy();
    });
  });
});
