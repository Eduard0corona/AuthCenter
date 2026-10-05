import { useId, useRef, useState } from "react";
import { apiRequest, ApiError } from "../api/client";
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

export function ReauthenticationDialog({ open, purpose, title, detail, confirmLabel, dangerous = false, onCancel, onProof }: ReauthenticationDialogProps) {
  const password = useRef<HTMLInputElement>(null);
  const dialog = useModalDialog(open, password);
  const titleId = useId();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  async function submit(event: React.FormEvent) { event.preventDefault(); const value = password.current?.value ?? ""; if (!value) { setError("Escribe tu contraseña actual."); return; } setBusy(true); setError(""); try { const proof = await apiRequest<ProofResponse>("/api/auth/reauth/password", { method: "POST", body: JSON.stringify({ purpose, password: value }) }); if (password.current) password.current.value = ""; await onProof(proof.proofToken); } catch (caught) { setError(caught instanceof ApiError ? caught.message : "No pudimos verificar tu identidad."); } finally { setBusy(false); } }
  return <dialog ref={dialog} className="dialog" aria-labelledby={titleId} onCancel={event => { event.preventDefault(); if (!busy) onCancel(); }}><form onSubmit={event => void submit(event)}><div className="dialog__content"><p className="eyebrow">Confirma tu identidad</p><h2 id={titleId}>{title}</h2><p>{detail}</p><label className="field"><span>Tu contraseña actual</span><input ref={password} type="password" autoComplete="current-password" disabled={busy} /></label><p className="muted">Se valida contra tu sesión administrativa y nunca corresponde a la cuenta objetivo.</p>{error ? <p className="alert alert--error" role="alert">{error}</p> : null}</div><div className="dialog__actions"><button className="button button--secondary" type="button" onClick={onCancel} disabled={busy}>Cancelar</button><button className={`button ${dangerous ? "button--danger" : ""}`} disabled={busy}>{busy ? "Verificando…" : confirmLabel}</button></div></form></dialog>;
}
