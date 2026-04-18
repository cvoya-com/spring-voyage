import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { RoutePlaceholder } from "./route-placeholder";

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: React.ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

describe("RoutePlaceholder", () => {
  it("renders title, description, and tracking issue links", () => {
    render(
      <RoutePlaceholder
        title="Inbox"
        description="Conversations awaiting a response from you."
        tracking={[
          { number: 447, label: "Inbox surface" },
          { number: 456, label: "CLI parity" },
        ]}
      />,
    );

    expect(screen.getByRole("heading", { name: "Inbox" })).toBeInTheDocument();
    expect(
      screen.getByText(/Conversations awaiting a response from you\./),
    ).toBeInTheDocument();
    expect(screen.getByText(/Not yet implemented/)).toBeInTheDocument();
    const link447 = screen.getByRole("link", { name: /#447 Inbox surface/ });
    expect(link447).toHaveAttribute(
      "href",
      "https://github.com/cvoya-com/spring-voyage/issues/447",
    );
    expect(
      screen.getByRole("link", { name: /#456 CLI parity/ }),
    ).toBeInTheDocument();
  });

  it("renders related links when provided", () => {
    render(
      <RoutePlaceholder
        title="Agents"
        description="Every agent across every unit."
        related={[
          { href: "/units", label: "Browse by unit" },
          { href: "/", label: "Dashboard" },
        ]}
      />,
    );

    expect(
      screen.getByRole("link", { name: "Browse by unit" }),
    ).toHaveAttribute("href", "/units");
    expect(
      screen.getByRole("link", { name: "Dashboard" }),
    ).toHaveAttribute("href", "/");
  });
});
