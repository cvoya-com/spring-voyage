import { describe, expect, it } from "vitest";

import {
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

    it("allows an empty unit list for a top-level tenant-parented agent", () => {
      expect(
        validateAgentCreateInput({
          displayName: "Ada",
          unitIds: ["", "  "],
        }),
      ).toBeNull();
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

  describe("buildAgentDefinitionJson", () => {
    it("returns null when no execution shorthand was supplied", () => {
      expect(buildAgentDefinitionJson({})).toBeNull();
      expect(
        buildAgentDefinitionJson({
          image: "  ",
          runtime: "",
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

    it("packs supplied fields into the direct create definition shape (ADR-0039 K6)", () => {
      const json = buildAgentDefinitionJson({
        image: "ghcr.io/example:latest",
        runtime: "claude-code",
        model: { provider: "anthropic", id: "claude-sonnet-4-6" },
      });
      expect(json).not.toBeNull();
      const parsed = JSON.parse(json as string);
      expect(parsed).toEqual({
        runtime: "claude-code",
        model: { provider: "anthropic", id: "claude-sonnet-4-6" },
        execution: {
          image: "ghcr.io/example:latest",
        },
      });
    });

    it("emits a complete structured model when provider and id are both supplied (ADR-0039 I1)", () => {
      const json = buildAgentDefinitionJson({
        model: { provider: "anthropic", id: "claude-3-5-sonnet" },
      });
      expect(JSON.parse(json as string)).toEqual({
        model: { provider: "anthropic", id: "claude-3-5-sonnet" },
      });
    });

    it("only includes fields that were actually supplied", () => {
      const json = buildAgentDefinitionJson({ runtime: "codex" });
      expect(JSON.parse(json as string)).toEqual({
        runtime: "codex",
      });
    });

    it("omits the structured model when only one half is supplied", () => {
      expect(
        JSON.parse(
          buildAgentDefinitionJson({
            runtime: "spring-voyage",
            model: { provider: "ollama", id: "" },
          }) as string,
        ),
      ).toEqual({ runtime: "spring-voyage" });

      expect(
        JSON.parse(
          buildAgentDefinitionJson({
            runtime: "spring-voyage",
            model: { provider: "", id: "llama3.2:3b" },
          }) as string,
        ),
      ).toEqual({ runtime: "spring-voyage" });
    });

    it("trims whitespace on provider and id", () => {
      const json = buildAgentDefinitionJson({
        model: { provider: "  anthropic  ", id: "  claude-opus-4-7  " },
      });
      expect(JSON.parse(json as string)).toEqual({
        model: { provider: "anthropic", id: "claude-opus-4-7" },
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
        runtime: "claude-code",
        hosting: null,
      });
      const parsed = JSON.parse(json as string);
      expect("execution" in parsed).toBe(false);
    });

    it("omits hosting from the serialised JSON when hosting is undefined", () => {
      const json = buildAgentDefinitionJson({ runtime: "claude-code" });
      const parsed = JSON.parse(json as string);
      expect("execution" in parsed).toBe(false);
    });

    it("returns null when only hosting is null and no other field was supplied", () => {
      expect(buildAgentDefinitionJson({ hosting: null })).toBeNull();
    });

    it("packs hosting alongside other execution fields", () => {
      const json = buildAgentDefinitionJson({
        image: "ghcr.io/example:latest",
        runtime: "claude-code",
        model: { provider: "anthropic", id: "claude-sonnet-4-6" },
        hosting: "persistent",
      });
      expect(JSON.parse(json as string)).toEqual({
        runtime: "claude-code",
        model: { provider: "anthropic", id: "claude-sonnet-4-6" },
        execution: {
          image: "ghcr.io/example:latest",
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
        definitionJson: null,
      });
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
        runtime: "claude-code",
        model: { provider: "anthropic", id: "claude-sonnet-4-6" },
      });
      expect(body.definitionJson).toBe(
        JSON.stringify({
          runtime: "claude-code",
          model: { provider: "anthropic", id: "claude-sonnet-4-6" },
        }),
      );
    });

    it("forwards hosting into definitionJson when supplied (ADR-0039 I2)", () => {
      const body = buildCreateAgentRequest({
        displayName: "Ada",
        unitIds: ["engineering"],
        runtime: "claude-code",
        hosting: "ephemeral",
      });
      expect(body.definitionJson).toBe(
        JSON.stringify({
          runtime: "claude-code",
          execution: {
            hosting: "ephemeral",
          },
        }),
      );
    });

    it("omits hosting from definitionJson when hosting is null (inherit) but other fields are set", () => {
      const body = buildCreateAgentRequest({
        displayName: "Ada",
        unitIds: ["engineering"],
        runtime: "claude-code",
        hosting: null,
      });
      expect(body.definitionJson).toBe(
        JSON.stringify({ runtime: "claude-code" }),
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

    it("includes an empty unitIds array and null definitionJson for top-level inheritance", () => {
      expect(
        buildCreateAgentRequest({
          displayName: "Ada",
          unitIds: [],
        }),
      ).toEqual({
        displayName: "Ada",
        description: "",
        role: null,
        unitIds: [],
        definitionJson: null,
      });
    });
  });

  describe("describeAgentCreateError", () => {
    it("returns copy for all defined error codes", () => {
      expect(describeAgentCreateError("displayName-required")).toBeTruthy();
    });
  });
});
