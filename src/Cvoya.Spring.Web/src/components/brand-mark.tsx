"use client";

import Image from "next/image";
import { useTheme } from "@/lib/theme";
import { cn } from "@/lib/utils";

interface BrandMarkProps {
  /**
   * Pixel size of the rendered mark. Both axes are equal — the asset is
   * square. Defaults to 24, which matches the sidebar header in the design
   * system.
   */
  size?: number;
  /** Optional accessible label. Defaults to "Spring Voyage". */
  label?: string;
  className?: string;
}

/**
 * Theme-aware Spring Voyage sailboat mark. Paints white-on-transparent in
 * dark mode and black-on-transparent in light mode; both PNGs ship from
 * `public/brand/` at build time so the swap is instant.
 */
export function BrandMark({
  size = 24,
  label = "Spring Voyage",
  className,
}: BrandMarkProps) {
  const { theme } = useTheme();
  // Exhaustive switch over the `Theme` union so adding a third value in
  // `theme.tsx` forces an update here at build time — a plain ternary
  // would silently collapse new values into the dark branch.
  const src = (() => {
    switch (theme) {
      case "light":
        return "/brand/sailboat-light.png";
      case "dark":
        return "/brand/sailboat-dark.png";
    }
  })();
  return (
    <Image
      src={src}
      width={size}
      height={size}
      alt={label}
      data-testid="brand-mark"
      data-theme={theme}
      // The asset is always square; `priority` would over-fetch on routes
      // that don't actually display the mark above the fold, so leave it
      // off — the sidebar load happens early enough that lazy is fine.
      className={cn("inline-block shrink-0 select-none", className)}
    />
  );
}
