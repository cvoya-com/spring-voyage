"use client";

import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  description?: string;
  /** Button label for the confirming action. Defaults to "Confirm". */
  confirmLabel?: string;
  /** Button label for the cancel action. Defaults to "Cancel". */
  cancelLabel?: string;
  /** Visual style for the confirm button. Defaults to "destructive". */
  confirmVariant?: "default" | "destructive";
  /** Invoked when the user confirms. Should return a promise — confirm is async-safe. */
  onConfirm: () => void | Promise<void>;
  /** Invoked when the user cancels or dismisses the dialog. */
  onCancel: () => void;
  /** Optional: when true, disable the confirm button (e.g., during the confirm call). */
  pending?: boolean;
}

/**
 * Narrow, opinionated confirmation dialog wrapper. Centralizes the "are you
 * sure?" pattern so callers don't reimplement the Cancel/Confirm footer on
 * every destructive action (remove member, delete secret, etc.).
 */
export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel = "Confirm",
  cancelLabel = "Cancel",
  confirmVariant = "destructive",
  onConfirm,
  onCancel,
  pending = false,
}: ConfirmDialogProps) {
  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title={title}
      description={description}
      footer={
        <>
          <Button
            variant="outline"
            onClick={onCancel}
            disabled={pending}
          >
            {cancelLabel}
          </Button>
          <Button
            variant={confirmVariant}
            onClick={() => {
              void onConfirm();
            }}
            disabled={pending}
          >
            {pending ? "…" : confirmLabel}
          </Button>
        </>
      }
    >
      <p className="sr-only">{description ?? title}</p>
    </Dialog>
  );
}
