// Responsive-smoke test for the Tabs primitive (PR-Q1 / #445).
//
// Unit detail carries 11 tab triggers. The `TabsList` must keep them
// reachable at a 375px viewport; the fix wraps the inner flex row in
// an `overflow-x-auto` scroll container so the bar scrolls
// horizontally instead of forcing the whole page off-screen.

import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import {
  assertNoOverflowingPixelWidths,
  assertResponsiveContainer,
} from "@/test/viewport";

import { Tabs, TabsContent, TabsList, TabsTrigger } from "./tabs";

describe("TabsList (responsive)", () => {
  it("wraps the inner flex row in an overflow-x-auto container", () => {
    const { container } = render(
      <Tabs defaultValue="general">
        <TabsList>
          {[
            "general",
            "agents",
            "sub-units",
            "skills",
            "policies",
            "connector",
            "secrets",
            "boundary",
            "expertise",
            "activity",
            "costs",
          ].map((v) => (
            <TabsTrigger key={v} value={v}>
              {v}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="general">general</TabsContent>
      </Tabs>,
    );

    // The outer wrapper must declare the scroll escape hatch so the
    // bar never pushes the viewport wider than the parent column.
    const wrapper = container.querySelector(".overflow-x-auto");
    expect(wrapper).not.toBeNull();
    assertResponsiveContainer(wrapper as HTMLElement);

    // No trigger should pin a fixed pixel width that would overflow a
    // 375px viewport.
    assertNoOverflowingPixelWidths(container, 375);
  });
});
