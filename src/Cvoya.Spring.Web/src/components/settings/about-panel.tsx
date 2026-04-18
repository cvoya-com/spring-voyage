"use client";

// About panel (Settings drawer / #451). Read-only platform metadata
// fetched from `GET /api/v1/platform/info`. CLI parity target:
// `spring platform info` — both surfaces consume the same endpoint so
// version reporting can't drift.

import { usePlatformInfo } from "@/lib/api/queries";

export function AboutPanel() {
  const query = usePlatformInfo();
  const info = query.data;

  if (query.isPending) {
    return (
      <p className="text-xs text-muted-foreground">Loading platform info…</p>
    );
  }

  // The endpoint is anonymous; a null result means "server too old /
  // unreachable". Surface the empty state rather than blocking the
  // drawer on a single panel's failure.
  const version = info?.version ?? "(unavailable)";
  const buildHash = info?.buildHash ?? null;
  const license = info?.license ?? "(unavailable)";

  return (
    <dl className="space-y-2">
      <InfoRow label="Version" value={version} testId="settings-about-version" />
      <InfoRow
        label="Build hash"
        value={buildHash ?? "(not embedded)"}
        mono={buildHash != null}
        testId="settings-about-build"
      />
      <InfoRow label="License" value={license} mono testId="settings-about-license" />
      <p className="pt-2 text-xs text-muted-foreground">
        Mirrors <code className="font-mono text-[11px]">spring platform info</code>.
      </p>
    </dl>
  );
}

function InfoRow({
  label,
  value,
  mono,
  testId,
}: {
  label: string;
  value: string;
  mono?: boolean;
  testId?: string;
}) {
  return (
    <div className="flex items-baseline justify-between gap-4">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd
        className={mono ? "font-mono text-xs" : "text-sm"}
        data-testid={testId}
      >
        {value}
      </dd>
    </div>
  );
}
