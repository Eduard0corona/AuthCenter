import { useId } from "react";
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
}

export function ConfirmDialog({ open, title, detail, confirmLabel, dangerous = false, busy = false, onConfirm, onCancel }: ConfirmDialogProps) {
  const dialogRef = useModalDialog(open);
  const titleId = useId();
  const detailId = useId();

  return (
    <dialog ref={dialogRef} className="dialog" aria-labelledby={titleId} aria-describedby={detailId} onCancel={(event) => { event.preventDefault(); if (!busy) onCancel(); }}>
      <div className="dialog__content">
        <p className="eyebrow">Confirmación requerida</p>
        <h2 id={titleId}>{title}</h2>
        <p id={detailId}>{detail}</p>
      </div>
      <div className="dialog__actions">
        <button className="button button--secondary" type="button" onClick={onCancel} disabled={busy}>Cancelar</button>
        <button className={`button ${dangerous ? "button--danger" : ""}`} type="button" onClick={onConfirm} disabled={busy}>
          {busy ? "Procesando…" : confirmLabel}
        </button>
      </div>
    </dialog>
  );
}
