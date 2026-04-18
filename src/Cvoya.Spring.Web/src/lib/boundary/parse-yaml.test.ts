// Unit tests for the client-side boundary YAML parser (#524).
//
// Coverage targets mirror the acceptance table in the issue:
// - happy-path parse round-trips both the camelCase CLI shape and the
//   snake_case manifest shape used by `spring apply -f`;
// - malformed YAML raises `BoundaryYamlParseError` rather than returning
//   garbage;
// - synthesis entries with blank names drop out silently (matches CLI
//   tolerance);
// - empty / `null` / `{}` documents map to an all-null boundary.

import { describe, expect, it } from "vitest";

import {
  BoundaryYamlParseError,
  parseBoundaryYaml,
} from "./parse-yaml";

describe("parseBoundaryYaml", () => {
  it("parses the CLI camelCase shape", () => {
    const yaml = `
opacities:
  - domainPattern: secret-*
    originPattern: agent://internal-*
projections:
  - domainPattern: react
    renameTo: frontend
    overrideLevel: expert
syntheses:
  - name: team-frontend
    domainPattern: react
    level: expert
`;
    const result = parseBoundaryYaml(yaml);
    expect(result.opacities).toEqual([
      { domainPattern: "secret-*", originPattern: "agent://internal-*" },
    ]);
    expect(result.projections).toEqual([
      {
        domainPattern: "react",
        originPattern: null,
        renameTo: "frontend",
        retag: null,
        overrideLevel: "expert",
      },
    ]);
    expect(result.syntheses).toEqual([
      {
        name: "team-frontend",
        domainPattern: "react",
        originPattern: null,
        description: null,
        level: "expert",
      },
    ]);
  });

  it("parses the snake_case manifest shape (spring apply -f)", () => {
    const yaml = `
opacities:
  - domain_pattern: secret-*
    origin_pattern: agent://internal-*
projections:
  - domain_pattern: react
    rename_to: frontend
    override_level: expert
syntheses:
  - name: team-frontend
    domain_pattern: react
    level: expert
`;
    const result = parseBoundaryYaml(yaml);
    expect(result.opacities?.[0]).toEqual({
      domainPattern: "secret-*",
      originPattern: "agent://internal-*",
    });
    expect(result.projections?.[0].renameTo).toBe("frontend");
    expect(result.projections?.[0].overrideLevel).toBe("expert");
    expect(result.syntheses?.[0].name).toBe("team-frontend");
  });

  it("accepts a nested `boundary:` block (bare)", () => {
    const yaml = `
boundary:
  opacities:
    - domainPattern: foo
`;
    expect(parseBoundaryYaml(yaml).opacities).toEqual([
      { domainPattern: "foo", originPattern: null },
    ]);
  });

  it("accepts a full unit manifest with `unit.boundary`", () => {
    const yaml = `
unit:
  name: eng-team
  boundary:
    opacities:
      - domain_pattern: secret-*
`;
    const result = parseBoundaryYaml(yaml);
    expect(result.opacities).toEqual([
      { domainPattern: "secret-*", originPattern: null },
    ]);
    expect(result.projections).toBeNull();
  });

  it("drops synthesis entries without a name", () => {
    const yaml = `
syntheses:
  - name: real
    domainPattern: foo
  - domainPattern: unnamed
  - name: "   "
`;
    const result = parseBoundaryYaml(yaml);
    expect(result.syntheses).toHaveLength(1);
    expect(result.syntheses?.[0].name).toBe("real");
  });

  it("returns all-null for an empty document", () => {
    expect(parseBoundaryYaml("")).toEqual({
      opacities: null,
      projections: null,
      syntheses: null,
    });
    expect(parseBoundaryYaml("# comment only\n")).toEqual({
      opacities: null,
      projections: null,
      syntheses: null,
    });
  });

  it("throws BoundaryYamlParseError on malformed YAML", () => {
    expect(() =>
      parseBoundaryYaml("opacities:\n  - domainPattern: [unterminated"),
    ).toThrow(BoundaryYamlParseError);
  });

  it("throws when the root is not a mapping", () => {
    expect(() => parseBoundaryYaml("- foo\n- bar")).toThrow(
      BoundaryYamlParseError,
    );
  });

  it("throws when a rule list is a scalar", () => {
    expect(() => parseBoundaryYaml("opacities: not-a-list")).toThrow(
      BoundaryYamlParseError,
    );
  });

  it("throws when an opacity entry is not a mapping", () => {
    expect(() => parseBoundaryYaml("opacities:\n  - scalar-string")).toThrow(
      BoundaryYamlParseError,
    );
  });

  it("coerces blank strings to null and collapses empty lists", () => {
    const yaml = `
opacities:
  - domainPattern: ""
    originPattern: agent://*
projections: []
syntheses: []
`;
    const result = parseBoundaryYaml(yaml);
    expect(result.opacities?.[0]).toEqual({
      domainPattern: null,
      originPattern: "agent://*",
    });
    expect(result.projections).toBeNull();
    expect(result.syntheses).toBeNull();
  });
});
