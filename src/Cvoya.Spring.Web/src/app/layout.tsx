import type { Metadata, Viewport } from "next";
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
  return (
    <html lang="en" suppressHydrationWarning>
      <body className="flex h-screen bg-background text-foreground antialiased dark">
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
