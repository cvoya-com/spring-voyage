import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { BrandMark } from "./brand-mark";

// next/image renders a real `<img>` after Next's image-optimizer middleware
// has resolved the src. In tests we don't run that pipeline, so collapse
// the component down to a passthrough `<img>` that preserves the src/alt
// pair we want to assert on.
vi.mock("next/image", () => ({
  default: ({
    src,
    alt,
    width,
    height,
    ...rest
  }: {
    src: string;
    alt: string;
    width: number;
    height: number;
  } & Record<string, unknown>) => (
    // eslint-disable-next-line @next/next/no-img-element
    <img src={src} alt={alt} width={width} height={height} {...rest} />
  ),
}));

const themeRef = { current: "dark" as "dark" | "light" };

vi.mock("@/lib/theme", () => ({
  useTheme: () => ({ theme: themeRef.current, toggleTheme: vi.fn() }),
}));

describe("BrandMark", () => {
  it("renders the dark-mode asset under the dark theme by default", () => {
    themeRef.current = "dark";
    render(<BrandMark />);
    const img = screen.getByTestId("brand-mark") as HTMLImageElement;
    expect(img).toHaveAttribute("src", "/brand/sailboat-dark.png");
    expect(img).toHaveAttribute("alt", "Spring Voyage");
    expect(img).toHaveAttribute("data-theme", "dark");
    expect(img).toHaveAttribute("width", "24");
    expect(img).toHaveAttribute("height", "24");
  });

  it("swaps to the light-mode asset when the theme flips to light", () => {
    themeRef.current = "light";
    render(<BrandMark />);
    const img = screen.getByTestId("brand-mark") as HTMLImageElement;
    expect(img).toHaveAttribute("src", "/brand/sailboat-light.png");
    expect(img).toHaveAttribute("data-theme", "light");
  });

  it("respects custom size and accessible label", () => {
    themeRef.current = "dark";
    render(<BrandMark size={40} label="Spring Voyage logo" />);
    const img = screen.getByTestId("brand-mark") as HTMLImageElement;
    expect(img).toHaveAttribute("width", "40");
    expect(img).toHaveAttribute("height", "40");
    expect(img).toHaveAttribute("alt", "Spring Voyage logo");
  });

  // Parameterised coverage that pins the src-per-theme contract — if a new
  // `Theme` value ever appears, the exhaustive switch in `brand-mark.tsx`
  // will fail to compile and this table will need a new row.
  it.each([
    ["dark", "/brand/sailboat-dark.png"],
    ["light", "/brand/sailboat-light.png"],
  ] as const)("maps the %s theme to %s", (theme, expectedSrc) => {
    themeRef.current = theme;
    render(<BrandMark />);
    const img = screen.getByTestId("brand-mark") as HTMLImageElement;
    expect(img).toHaveAttribute("src", expectedSrc);
    expect(img).toHaveAttribute("data-theme", theme);
  });
});
