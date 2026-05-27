// Tests for <HumanFromSelector> (ADR-0062 § 5, #2807 + #2829).
//
// The selector is presentation-only — consumers pass the resolved
// caller-Hats set and own the selected-id state. These tests pin the
// three rendering modes (zero / one / many) plus the helper export
// (default-pick rule) so the composers and inbox chip can rely on
// stable formatting.
//
// #2829: the selector renders the server-supplied `disambiguatedLabel`
// verbatim. The old `formatHumanContext` / `formatHumanLabel` helpers
// are gone with their tests — the disambiguation rule lives server-
// side and is covered by `HatLabelDisambiguator` unit tests + the
// `TenantUserIdentityEndpoints` integration tests.

import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import {
  HumanFromSelector,
  pickDefaultHumanId,
} from "./human-from-selector";
import type { CallerHumanResponse } from "@/lib/api/types";

const BOB: CallerHumanResponse = {
  humanId: "00000000-0000-0000-0000-0000000000b0",
  displayName: "Bob",
  disambiguatedLabel: "Bob",
  isPrimary: true,
  memberships: [
    {
      unitId: "00000000-0000-0000-0000-0000000000a0",
      unitDisplayName: "Magazine",
      roles: ["designer"],
    },
  ],
};

const ALICE: CallerHumanResponse = {
  humanId: "00000000-0000-0000-0000-0000000000a1",
  displayName: "Alice",
  disambiguatedLabel: "Alice",
  isPrimary: false,
  memberships: [
    {
      unitId: "00000000-0000-0000-0000-0000000000a2",
      unitDisplayName: "Editorial",
      roles: ["reviewer", "security_lead"],
    },
  ],
};

const PLAIN: CallerHumanResponse = {
  humanId: "00000000-0000-0000-0000-0000000000c0",
  displayName: "Casey",
  disambiguatedLabel: "Casey",
  isPrimary: false,
  memberships: [],
};

describe("pickDefaultHumanId", () => {
  it("returns null when the bound set is empty", () => {
    expect(pickDefaultHumanId([], null)).toBeNull();
  });

  it("prefers the supplied hint when it is bound", () => {
    expect(pickDefaultHumanId([BOB, ALICE], ALICE.humanId)).toBe(ALICE.humanId);
  });

  it("falls back to the primary Hat when the hint is null", () => {
    expect(pickDefaultHumanId([ALICE, BOB], null)).toBe(BOB.humanId);
  });

  it("falls back to the primary Hat when the hint is unbound", () => {
    expect(pickDefaultHumanId([BOB], "unbound-id")).toBe(BOB.humanId);
  });

  it("falls back to the first Hat when no primary is pinned", () => {
    expect(pickDefaultHumanId([ALICE, PLAIN], null)).toBe(ALICE.humanId);
  });
});

describe("HumanFromSelector — rendering modes", () => {
  it("renders nothing when the bound set is empty", () => {
    const { container } = render(
      <HumanFromSelector humans={[]} value={null} onChange={() => {}} />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("collapses to a static badge for one bound Hat", () => {
    render(
      <HumanFromSelector
        humans={[BOB]}
        value={BOB.humanId}
        onChange={() => {}}
      />,
    );
    const root = screen.getByTestId("human-from-selector");
    expect(root).toHaveAttribute("data-mode", "static");
    expect(root).toHaveTextContent("As");
    expect(root).toHaveTextContent("Bob");
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
  });

  it("renders the server-supplied disambiguated label verbatim (collapsed)", () => {
    const colliding: CallerHumanResponse = {
      ...BOB,
      disambiguatedLabel: "Bob — designer",
    };
    render(
      <HumanFromSelector
        humans={[colliding]}
        value={colliding.humanId}
        onChange={() => {}}
      />,
    );
    expect(screen.getByTestId("human-from-selector")).toHaveTextContent(
      "Bob — designer",
    );
  });

  it("renders a picker for 2+ bound Hats and emits the chosen id", () => {
    const onChange = vi.fn();
    render(
      <HumanFromSelector
        humans={[BOB, ALICE]}
        value={BOB.humanId}
        onChange={onChange}
      />,
    );
    const root = screen.getByTestId("human-from-selector");
    expect(root).toHaveAttribute("data-mode", "picker");

    const select = screen.getByTestId("human-from-selector-select");
    expect(select).toHaveValue(BOB.humanId);

    fireEvent.change(select, { target: { value: ALICE.humanId } });
    expect(onChange).toHaveBeenCalledWith(ALICE.humanId);
  });

  it("renders each picker option's disambiguatedLabel verbatim", () => {
    const bobDesigner: CallerHumanResponse = {
      ...BOB,
      isPrimary: false,
      disambiguatedLabel: "Bob — designer",
    };
    const bobReviewer: CallerHumanResponse = {
      ...BOB,
      humanId: "00000000-0000-0000-0000-0000000000b1",
      isPrimary: false,
      disambiguatedLabel: "Bob — reviewer",
    };
    render(
      <HumanFromSelector
        humans={[bobDesigner, bobReviewer]}
        value={bobDesigner.humanId}
        onChange={() => {}}
      />,
    );
    const options = screen
      .getAllByRole("option")
      .map((el) => (el as HTMLOptionElement).textContent);
    expect(options).toContain("Bob — designer");
    expect(options).toContain("Bob — reviewer");
  });

  it("orders primary Hat first then alphabetical", () => {
    render(
      <HumanFromSelector
        humans={[ALICE, BOB]}
        value={BOB.humanId}
        onChange={() => {}}
      />,
    );
    const options = screen
      .getAllByRole("option")
      .map((el) => (el as HTMLOptionElement).value);
    expect(options[0]).toBe(BOB.humanId);
    expect(options[1]).toBe(ALICE.humanId);
  });

  it("exposes a primary badge when the selection is the primary Hat", () => {
    render(
      <HumanFromSelector
        humans={[BOB, ALICE]}
        value={BOB.humanId}
        onChange={() => {}}
      />,
    );
    expect(
      screen.getByTestId("human-from-selector-primary-hint"),
    ).toBeInTheDocument();
  });

  it("hides the primary badge when a non-primary Hat is selected", () => {
    render(
      <HumanFromSelector
        humans={[BOB, ALICE]}
        value={ALICE.humanId}
        onChange={() => {}}
      />,
    );
    expect(
      screen.queryByTestId("human-from-selector-primary-hint"),
    ).not.toBeInTheDocument();
  });

  it("respects the disabled prop on the select", () => {
    render(
      <HumanFromSelector
        humans={[BOB, ALICE]}
        value={BOB.humanId}
        onChange={() => {}}
        disabled
      />,
    );
    expect(screen.getByTestId("human-from-selector-select")).toBeDisabled();
  });
});
