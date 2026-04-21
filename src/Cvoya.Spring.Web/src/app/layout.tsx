import type { Metadata, Viewport } from "next";
import { GeistMono } from "geist/font/mono";
import { GeistSans } from "geist/font/sans";
import { ThemeProvider } from "@/lib/theme";
import { ToastProvider } from "@/components/ui/toast";
import { AppShell } from "@/components/app-shell";
import { QueryProvider } from "@/lib/api/query-provider";
import "./globals.css";

export const metadata: Metadata = {
  title: "Spring Voyage",
  description: "AI agent orchestration dashboard",
};

export const viewport: Viewport = {
  themeColor: "#09090b",
  width: "device-width",
  initialScale: 1,
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  // The `dark` class is the SSR default so `--sv-*` and `--color-*` tokens
  // paint consistently before hydration; the ThemeProvider flips it on the
  // same `<html>` element post-mount so the cascade reaches `<body>` and
  // its subtree in one place.
  return (
    <html
      lang="en"
      suppressHydrationWarning
      className={`dark ${GeistSans.variable} ${GeistMono.variable}`}
    >
      <body className="flex h-screen bg-background text-foreground font-sans antialiased">
        <ThemeProvider>
          <QueryProvider>
            <ToastProvider>
              <AppShell>{children}</AppShell>
            </ToastProvider>
          </QueryProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}
