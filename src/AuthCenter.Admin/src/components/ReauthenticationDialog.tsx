import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { ApiError, apiRequest, redirectToLoginOnce } from "../api/client";
import { errorMessage } from "../api/errors";
import { useModalDialog } from "../hooks/useModalDialog";

interface ReauthenticationDialogProps {
  open: boolean;
  purpose: string;
  title: string;
  detail: string;
  confirmLabel: string;
  dangerous?: boolean;
  onCancel: () => void;
  onProof: (proofToken: string) => Promise<void>;
}

interface ProofResponse { proofToken: string; assuranceLevel: string; expiresIn: number; }

const WRONG_PASSWORD = "La contraseña no es correcta. Inténtalo de nuevo.";
const SESSION_ENDED = "Tu sesión terminó. Inicia sesión de nuevo para continuar.";

/** The answer to a wrong password: a 400 INVALID_REAUTHENTICATION (older servers sent it as a 401). */
function isWrongPassword(error: unknown): boolean {
  return error instanceof ApiError && error.code === "INVALID_REAUTHENTICATION";
}

/**
 * Asks for the operator's password before a sensitive action and hands its single-use proof to
 * onProof. A mistyped password stays here: the page behind, and what was typed in it, is kept.
 */
export function ReauthenticationDialog({ open, purpose, title, detail, confirmLabel, dangerous = false, onCancel, onProof }: ReauthenticationDialogProps) {
  const password = useRef<HTMLInputElement>(null);
  const dialog = useModalDialog(open, password);
  const titleId = useId();
  const passwordId = useId();
  const errorId = useId();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [sessionEnded, setSessionEnded] = useState(false);
  // Every wrong password clears the field; focus returns to it once it is enabled again.
  const [wrongAttempts, setWrongAttempts] = useState(0);
  useEffect(() => { if (wrongAttempts > 0) password.current?.focus(); }, [wrongAttempts]);
  // Opening the dialog again starts without the previous attempt's message.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    setError("");
    setSessionEnded(false);
  }

  async function submit(event: FormEvent): Promise<void> {
    event.preventDefault();
    const value = password.current?.value ?? "";
    if (!value) {
      setError("Escribe tu contraseña actual.");
      password.current?.focus();
      return;
    }
    setBusy(true);
    setError("");
    setSessionEnded(false);
    let proof: ProofResponse;
    try {
      proof = await apiRequest<ProofResponse>("/api/auth/reauth/password", { method: "POST", body: JSON.stringify({ purpose, password: value }) });
    } catch (caught) {
      setBusy(false);
      if (isWrongPassword(caught)) {
        if (password.current) password.current.value = "";
        setError(WRONG_PASSWORD);
        setWrongAttempts((count) => count + 1);
      } else if (caught instanceof ApiError && caught.kind === "authentication") {
        setSessionEnded(true);
        setError(SESSION_ENDED);
      } else {
        setError(errorMessage(caught));
      }
      return;
    }
    if (password.current) password.current.value = "";
    try {
      await onProof(proof.proofToken);
    } catch (caught) {
      setError(errorMessage(caught));
    } finally {
      setBusy(false);
    }
  }

  return (
    <dialog ref={dialog} className="dialog" aria-labelledby={titleId} onCancel={(event) => { event.preventDefault(); if (!busy) onCancel(); }}>
      <form onSubmit={(event) => void submit(event)}>
        <div className="dialog__content">
          <p className="eyebrow">Confirma tu identidad</p>
          <h2 id={titleId}>{title}</h2>
          <p>{detail}</p>
          <label className="field" htmlFor={passwordId}>
            <span>Tu contraseña actual</span>
            <input
              id={passwordId}
              ref={password}
              type="password"
              autoComplete="current-password"
              disabled={busy}
              aria-invalid={error === WRONG_PASSWORD || undefined}
              aria-describedby={error ? errorId : undefined}
            />
          </label>
          <p className="muted">Es la contraseña con la que entraste a esta consola, no la de la cuenta que estás cambiando.</p>
          {error ? <p className="alert alert--error" role="alert" id={errorId}>{error}</p> : null}
          {sessionEnded ? <button className="button button--secondary" type="button" onClick={redirectToLoginOnce}>Iniciar sesión de nuevo</button> : null}
        </div>
        <div className="dialog__actions">
          <button className="button button--secondary" type="button" onClick={onCancel} disabled={busy}>Cancelar</button>
          <button className={`button ${dangerous ? "button--danger" : ""}`} disabled={busy}>{busy ? "Verificando…" : confirmLabel}</button>
        </div>
      </form>
    </dialog>
  );
}
