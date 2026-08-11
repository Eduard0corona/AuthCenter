import { useEffect, useRef } from "react";

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
  const dialogRef = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog ref={dialogRef} className="dialog" onCancel={(event) => { event.preventDefault(); if (!busy) onCancel(); }}>
      <div className="dialog__content">
        <p className="eyebrow">Confirmación requerida</p>
        <h2>{title}</h2>
        <p>{detail}</p>
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
