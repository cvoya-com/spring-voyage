"use client";

import { useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import {
  ArrowLeft,
  Check,
  FileCode,
  FileText,
  Github,
  KeyRound,
  Lock,
  Rocket,
  Sparkles,
} from "lucide-react";

import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { cn } from "@/lib/utils";

// Default matches the platform-wide default model hint. Keep in sync with
// Cvoya.Spring.Dapr/Execution/AiProviderOptions.cs.
const DEFAULT_MODEL = "claude-sonnet-4-20250514";
const DEFAULT_COLOR = "#6366f1";

const NAME_PATTERN = /^[a-z0-9-]+$/;

// NOTE: `#XXX` placeholders below must be replaced with real follow-up issue
// numbers after this PR lands. Keep the literal string `#XXX` so a single
// find/replace updates every occurrence.

type Step = 1 | 2 | 3 | 4 | 5;
type Mode = "template" | "scratch" | "yaml";

const STEP_LABELS: Record<Step, string> = {
  1: "Details",
  2: "Mode",
  3: "GitHub",
  4: "Secrets",
  5: "Finalize",
};

interface FormState {
  name: string;
  displayName: string;
  description: string;
  model: string;
  color: string;
  mode: Mode | null;
}

const INITIAL_FORM: FormState = {
  name: "",
  displayName: "",
  description: "",
  model: DEFAULT_MODEL,
  color: DEFAULT_COLOR,
  mode: null,
};

function StepIndicator({ current }: { current: Step }) {
  const steps: Step[] = [1, 2, 3, 4, 5];
  return (
    <div className="sticky top-0 z-10 -mx-4 md:-mx-6 bg-background/80 backdrop-blur border-b border-border px-4 md:px-6 py-3">
      <ol className="flex items-center gap-2 overflow-x-auto">
        {steps.map((n, idx) => {
          const done = n < current;
          const active = n === current;
          return (
            <li key={n} className="flex items-center gap-2 whitespace-nowrap">
              <span
                className={cn(
                  "flex h-6 w-6 items-center justify-center rounded-full text-xs font-semibold",
                  done && "bg-primary text-primary-foreground",
                  active && "bg-primary/20 text-primary ring-2 ring-primary",
                  !done && !active && "bg-muted text-muted-foreground",
                )}
              >
                {done ? <Check className="h-3.5 w-3.5" /> : n}
              </span>
              <span
                className={cn(
                  "text-sm",
                  active ? "font-medium text-foreground" : "text-muted-foreground",
                )}
              >
                {STEP_LABELS[n]}
              </span>
              {idx < steps.length - 1 && (
                <span className="mx-1 h-px w-6 bg-border" aria-hidden />
              )}
            </li>
          );
        })}
      </ol>
    </div>
  );
}

export default function CreateUnitPage() {
  const router = useRouter();
  const { toast } = useToast();
  const [step, setStep] = useState<Step>(1);
  const [form, setForm] = useState<FormState>(INITIAL_FORM);
  const [stepError, setStepError] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const update = <K extends keyof FormState>(key: K, value: FormState[K]) => {
    setForm((prev) => ({ ...prev, [key]: value }));
  };

  const validateStep1 = (): string | null => {
    if (!form.name.trim()) return "Name is required.";
    if (!NAME_PATTERN.test(form.name))
      return "Name must be URL-safe (lowercase letters, digits, and hyphens).";
    return null;
  };

  const validateStep2 = (): string | null => {
    if (form.mode !== "scratch") {
      // Template and YAML are disabled in this pass.
      return "Select a mode to continue. Only Scratch is available right now.";
    }
    return null;
  };

  const handleNext = () => {
    setStepError(null);
    if (step === 1) {
      const err = validateStep1();
      if (err) {
        setStepError(err);
        return;
      }
    }
    if (step === 2) {
      const err = validateStep2();
      if (err) {
        setStepError(err);
        return;
      }
    }
    if (step < 5) setStep((s) => (s + 1) as Step);
  };

  const handleBack = () => {
    setStepError(null);
    setSubmitError(null);
    if (step > 1) setStep((s) => (s - 1) as Step);
  };

  const handleCreate = async () => {
    setSubmitError(null);
    setSubmitting(true);
    try {
      const created = await api.createUnit({
        name: form.name.trim(),
        displayName: form.displayName.trim() || form.name.trim(),
        description: form.description.trim(),
        model: form.model.trim() || undefined,
        color: form.color.trim() || undefined,
      });
      toast({ title: "Unit created", description: created.name });
      router.push(`/units/${encodeURIComponent(created.name)}`);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setSubmitError(message);
      toast({
        title: "Failed to create unit",
        description: message,
        variant: "destructive",
      });
    } finally {
      setSubmitting(false);
    }
  };

  const canGoNext = useMemo(() => {
    if (step === 1) return form.name.trim().length > 0;
    if (step === 2) return form.mode === "scratch";
    return true;
  }, [step, form]);

  return (
    <div className="space-y-6">
      <Link
        href="/units"
        className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
      >
        <ArrowLeft className="h-4 w-4" /> Units
      </Link>

      <div>
        <h1 className="text-2xl font-bold">Create a unit</h1>
        <p className="text-sm text-muted-foreground">
          Register a new unit and wire up its runtime configuration.
        </p>
      </div>

      <StepIndicator current={step} />

      {step === 1 && (
        <Card>
          <CardHeader>
            <CardTitle>Details</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                Name<span className="text-destructive"> *</span>
              </span>
              <Input
                value={form.name}
                onChange={(e) => update("name", e.target.value)}
                placeholder="engineering-team"
                autoFocus
              />
              <span className="block text-xs text-muted-foreground">
                URL-safe: lowercase letters, digits, and hyphens only. Used as
                the unit&apos;s address.
              </span>
            </label>

            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                Display name
              </span>
              <Input
                value={form.displayName}
                onChange={(e) => update("displayName", e.target.value)}
                placeholder="Engineering Team"
              />
            </label>

            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">Description</span>
              <Input
                value={form.description}
                onChange={(e) => update("description", e.target.value)}
                placeholder="Ships the core product."
              />
            </label>

            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <label className="block space-y-1">
                <span className="text-sm text-muted-foreground">Model</span>
                <Input
                  value={form.model}
                  onChange={(e) => update("model", e.target.value)}
                  placeholder={DEFAULT_MODEL}
                />
              </label>

              <label className="block space-y-1">
                <span className="text-sm text-muted-foreground">Color</span>
                <div className="flex items-center gap-2">
                  <input
                    type="color"
                    value={form.color}
                    onChange={(e) => update("color", e.target.value)}
                    className="h-9 w-12 cursor-pointer rounded border border-input bg-background p-1"
                    aria-label="Pick color"
                  />
                  <Input
                    value={form.color}
                    onChange={(e) => update("color", e.target.value)}
                    placeholder={DEFAULT_COLOR}
                  />
                </div>
              </label>
            </div>

            {stepError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {stepError}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {step === 2 && (
        <Card>
          <CardHeader>
            <CardTitle>Choose a mode</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <ModeCard
              icon={<FileText className="h-5 w-5" />}
              title="Template"
              description="Start from a pre-built team template."
              disabled
              // TODO: replace #XXX with the template catalog follow-up issue.
              tooltip="Template catalog is tracked in follow-up #XXX."
              selected={false}
              onSelect={() => {}}
            />
            <ModeCard
              icon={<Sparkles className="h-5 w-5" />}
              title="Scratch"
              description="Create an empty unit you can configure manually."
              selected={form.mode === "scratch"}
              onSelect={() => update("mode", "scratch")}
            />
            <ModeCard
              icon={<FileCode className="h-5 w-5" />}
              title="YAML"
              description="Import an existing unit manifest."
              disabled
              // TODO: replace #XXX with the YAML import follow-up issue.
              tooltip="YAML import is tracked in follow-up #XXX."
              selected={false}
              onSelect={() => {}}
            />

            {stepError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {stepError}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {step === 3 && (
        <Card className="bg-muted/40">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Github className="h-5 w-5" /> GitHub setup
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm text-muted-foreground">
            <p>
              GitHub App installation flow is tracked in follow-up{" "}
              {/* TODO: replace #XXX with the GitHub App install follow-up issue. */}
              <span className="font-mono text-foreground">#XXX</span>. Skip for
              now — you can finish creating the unit without it.
            </p>
            <Button onClick={handleNext}>Skip for now</Button>
          </CardContent>
        </Card>
      )}

      {step === 4 && (
        <Card className="bg-muted/40">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <KeyRound className="h-5 w-5" /> Secrets
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm text-muted-foreground">
            <p>
              Unit secrets CRUD is tracked in follow-up{" "}
              {/* TODO: replace #XXX with the unit secrets follow-up issue. */}
              <span className="font-mono text-foreground">#XXX</span>. Skip for
              now — you can finish creating the unit without it.
            </p>
            <Button onClick={handleNext}>Skip for now</Button>
          </CardContent>
        </Card>
      )}

      {step === 5 && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Rocket className="h-5 w-5" /> Finalize
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4 text-sm">
            <div className="rounded-md border border-border p-3 space-y-1">
              <SummaryRow label="Name" value={form.name} />
              <SummaryRow
                label="Display name"
                value={form.displayName || form.name}
              />
              <SummaryRow
                label="Description"
                value={form.description || "—"}
              />
              <SummaryRow label="Model" value={form.model || DEFAULT_MODEL} />
              <SummaryRow label="Color" value={form.color || DEFAULT_COLOR} />
              <SummaryRow
                label="Mode"
                value={form.mode ? form.mode : "—"}
              />
            </div>

            {submitError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {submitError}
              </p>
            )}

            <Button onClick={handleCreate} disabled={submitting}>
              {submitting ? "Creating…" : "Create unit"}
            </Button>
          </CardContent>
        </Card>
      )}

      <div className="flex items-center justify-between">
        <Button
          variant="outline"
          onClick={handleBack}
          disabled={step === 1 || submitting}
        >
          Back
        </Button>
        {/* Steps 3 and 4 are placeholders with an embedded "Skip for now"
            primary action — don't show a second Next button there. */}
        {step !== 3 && step !== 4 && step < 5 && (
          <Button onClick={handleNext} disabled={!canGoNext}>
            Next
          </Button>
        )}
      </div>
    </div>
  );
}

function ModeCard({
  icon,
  title,
  description,
  selected,
  disabled,
  tooltip,
  onSelect,
}: {
  icon: React.ReactNode;
  title: string;
  description: string;
  selected: boolean;
  disabled?: boolean;
  tooltip?: string;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      onClick={() => !disabled && onSelect()}
      disabled={disabled}
      title={tooltip}
      className={cn(
        "flex w-full items-start gap-3 rounded-md border p-4 text-left transition-colors",
        selected
          ? "border-primary bg-primary/5"
          : "border-border hover:bg-accent/50",
        disabled && "cursor-not-allowed opacity-60 hover:bg-transparent",
      )}
    >
      <div
        className={cn(
          "mt-0.5 rounded-md bg-muted p-2",
          selected && "bg-primary/15 text-primary",
        )}
      >
        {icon}
      </div>
      <div className="flex-1">
        <div className="flex items-center gap-2">
          <span className="text-sm font-medium">{title}</span>
          {disabled && (
            <span className="inline-flex items-center gap-1 rounded-full border border-border px-1.5 py-0.5 text-[10px] text-muted-foreground">
              <Lock className="h-3 w-3" /> Coming soon
            </span>
          )}
        </div>
        <div className="text-xs text-muted-foreground">{description}</div>
        {disabled && tooltip && (
          <div className="mt-1 text-[11px] text-muted-foreground">
            {tooltip}
          </div>
        )}
      </div>
    </button>
  );
}

function SummaryRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between gap-4">
      <span className="text-muted-foreground">{label}</span>
      <span className="font-mono text-xs text-right">{value}</span>
    </div>
  );
}
