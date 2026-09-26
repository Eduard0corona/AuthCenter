import { useId, useRef, useState } from "react";
import { useModalDialog } from "../hooks/useModalDialog";

interface DecisionDialogProps {
  open: boolean;
  title: string;
  detail: string;
  confirmLabel: string;
  /** Label of the comment box, and whether a comment is required. */
  commentLabel: string;
  commentRequired?: boolean;
  dangerous?: boolean;
  busy?: boolean;
  error?: string | null;
  onConfirm: (comment: string) => void;
  onCancel: () => void;
}

/** A decision with a comment (the reason of a rejection, a note on an approval). */
export function DecisionDialog({ open, title, detail, confirmLabel, commentLabel, commentRequired = false, dangerous = false, busy = false, error, onConfirm, onCancel }: DecisionDialogProps) {
  const comment = useRef<HTMLTextAreaElement>(null);
  const dialogRef = useModalDialog(open, comment);
  const titleId = useId();
  const detailId = useId();
  const commentId = useId();
  const [missing, setMissing] = useState(false);

  function confirm(): void {
    const value = comment.current?.value.trim() ?? "";
    if (commentRequired && !value) { setMissing(true); comment.current?.focus(); return; }
    setMissing(false);
    onConfirm(value);
  }

  return (
    <dialog ref={dialogRef} className="dialog" aria-labelledby={titleId} aria-describedby={detailId} onCancel={(event) => { event.preventDefault(); if (!busy) onCancel(); }}>
      <form className="dialog__content" onSubmit={(event) => { event.preventDefault(); confirm(); }}>
        <h2 id={titleId}>{title}</h2>
        <p id={detailId}>{detail}</p>
        <label className="field" htmlFor={commentId}>
          <span>{commentLabel}</span>
          <textarea id={commentId} ref={comment} rows={3} maxLength={1000} aria-invalid={missing || undefined} defaultValue="" key={open ? "open" : "closed"} />
          {missing ? <span className="field-error">Escribe el motivo: la persona lo leerá.</span> : null}
        </label>
        {error ? <p className="alert alert--error" role="alert">{error}</p> : null}
        <div className="dialog__actions">
          <button className="button button--secondary" type="button" onClick={onCancel} disabled={busy}>Cancelar</button>
          <button className={`button ${dangerous ? "button--danger" : ""}`} type="submit" disabled={busy}>{busy ? "Procesando…" : confirmLabel}</button>
        </div>
      </form>
    </dialog>
  );
}
