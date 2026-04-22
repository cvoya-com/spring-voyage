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
    it("requires an id", () => {
      expect(
        validateAgentCreateInput({ id: "  ", displayName: "x", unitIds: ["u"] }),
      ).toBe("id-required");
    });

    it("rejects ids with uppercase or punctuation", () => {
      expect(
        validateAgentCreateInput({
          id: "Bad_Name",
          displayName: "x",
          unitIds: ["u"],
        }),
      ).toBe("id-pattern");
    });

    it("requires a display name", () => {
      expect(
        validateAgentCreateInput({
          id: "ada",
          displayName: " ",
          unitIds: ["u"],
        }),
      ).toBe("displayName-required");
    });

    it("requires at least one non-empty unit", () => {
      expect(
        validateAgentCreateInput({
          id: "ada",
          displayName: "Ada",
          unitIds: ["", "  "],
        }),
      ).toBe("unit-required");
    });

    it("returns null for a well-formed input", () => {
      expect(
        validateAgentCreateInput({
          id: "ada",
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

    it("packs supplied fields under an `execution` block", () => {
      const json = buildAgentDefinitionJson({
        image: "ghcr.io/example:latest",
        runtime: "docker",
        tool: "claude-code",
        model: "claude-sonnet-4-6",
      });
      expect(json).not.toBeNull();
      const parsed = JSON.parse(json as string);
      expect(parsed).toEqual({
        execution: {
          image: "ghcr.io/example:latest",
          runtime: "docker",
          tool: "claude-code",
          model: "claude-sonnet-4-6",
        },
      });
    });

    it("only includes fields that were actually supplied", () => {
      const json = buildAgentDefinitionJson({ tool: "codex" });
      expect(JSON.parse(json as string)).toEqual({
        execution: { tool: "codex" },
      });
    });
  });

  describe("buildCreateAgentRequest", () => {
    it("trims whitespace and omits definitionJson when no exec shorthand was set", () => {
      const body = buildCreateAgentRequest({
        id: "  ada  ",
        displayName: "  Ada  ",
        role: "  reviewer  ",
        unitIds: ["  engineering  ", " marketing "],
      });
      expect(body).toEqual({
        name: "ada",
        displayName: "Ada",
        description: "",
        role: "reviewer",
        unitIds: ["engineering", "marketing"],
      });
      expect("definitionJson" in body).toBe(false);
    });

    it("normalises an empty role to null", () => {
      const body = buildCreateAgentRequest({
        id: "ada",
        displayName: "Ada",
        role: "   ",
        unitIds: ["engineering"],
      });
      expect(body.role).toBeNull();
    });

    it("includes definitionJson when execution shorthand is provided", () => {
      const body = buildCreateAgentRequest({
        id: "ada",
        displayName: "Ada",
        unitIds: ["engineering"],
        tool: "claude-code",
        model: "claude-sonnet-4-6",
      });
      expect(body.definitionJson).toBe(
        JSON.stringify({
          execution: { tool: "claude-code", model: "claude-sonnet-4-6" },
        }),
      );
    });

    it("throws on invalid input so the caller can surface the message", () => {
      expect(() =>
        buildCreateAgentRequest({
          id: "BadName",
          displayName: "x",
          unitIds: ["u"],
        }),
      ).toThrow(describeAgentCreateError("id-pattern"));
    });
  });
});
