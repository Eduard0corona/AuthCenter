import { useEffect, useRef, useState } from "react";

interface SecretRevealDialogProps {
  open: boolean;
  secret: string;
  title: string;
  onClose: () => void;
}

export function SecretRevealDialog({ open, secret, title, onClose }: SecretRevealDialogProps) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [copied, setCopied] = useState(false);
  const [copyError, setCopyError] = useState(false);

  useEffect(() => {
    const node = dialog.current;
    if (!node) return;
    if (open && !node.open) node.showModal();
    if (!open && node.open) node.close();
  }, [open]);

  async function copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(secret);
      setCopied(true);
      setCopyError(false);
    } catch {
      setCopyError(true);
    }
  }

  return <dialog ref={dialog} className="dialog" onCancel={(event) => event.preventDefault()}>
    <div className="dialog__content">
      <p className="eyebrow">Secreto de un solo uso</p>
      <h2>{title}</h2>
      <p>Guárdalo ahora en un gestor seguro. AuthCenter no podrá volver a mostrar este valor.</p>
      <div className="secret-reveal"><code>{secret}</code></div>
      {copyError ? <p className="alert alert--error" role="alert">No pudimos copiarlo automáticamente. Selecciona el valor y cópialo manualmente.</p> : null}
      {copied ? <p className="alert alert--success" role="status">Secreto copiado. Evita pegarlo en chats, tickets o logs.</p> : <p className="alert alert--warning">Cerrar esta ventana elimina el secreto de la memoria de la consola.</p>}
    </div>
    <div className="dialog__actions">
      <button className="button button--secondary" type="button" onClick={() => void copy()}>{copied ? "Copiado" : "Copiar secreto"}</button>
      <button className="button" type="button" onClick={() => { setCopied(false); setCopyError(false); onClose(); }}>Ya guardé el secreto</button>
    </div>
  </dialog>;
}
