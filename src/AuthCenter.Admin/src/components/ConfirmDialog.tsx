import { useId, useState } from "react";
import { errorMessage } from "../api/errors";
import { useModalDialog } from "../hooks/useModalDialog";

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  detail: string;
  confirmLabel: string;
  dangerous?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
  /** The confirmed operation's error. The dialog covers the page, so it shows the error itself. */
  error?: unknown;
}

export function ConfirmDialog({ open, title, detail, confirmLabel, dangerous = false, busy = false, onConfirm, onCancel, error }: ConfirmDialogProps) {
  const dialogRef = useModalDialog(open);
  const titleId = useId();
  const detailId = useId();
  // Only the error of a confirmation made while the dialog is open; opening it again starts clean.
  const [confirmed, setConfirmed] = useState(false);
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    setConfirmed(false);
  }
  const failure = confirmed && !busy && error ? errorMessage(error) : null;

  return (
    <dialog ref={dialogRef} className="dialog" aria-labelledby={titleId} aria-describedby={detailId} onCancel={(event) => { event.preventDefault(); if (!busy) onCancel(); }}>
      <div className="dialog__content">
        <p className="eyebrow">Confirmación requerida</p>
        <h2 id={titleId}>{title}</h2>
        <p id={detailId}>{detail}</p>
        {failure ? <p className="alert alert--error" role="alert">{failure}</p> : null}
      </div>
      <div className="dialog__actions">
        <button className="button button--secondary" type="button" onClick={onCancel} disabled={busy}>Cancelar</button>
        <button className={`button ${dangerous ? "button--danger" : ""}`} type="button" onClick={() => { setConfirmed(true); onConfirm(); }} disabled={busy}>
          {busy ? "Procesando…" : confirmLabel}
        </button>
      </div>
    </dialog>
  );
}
