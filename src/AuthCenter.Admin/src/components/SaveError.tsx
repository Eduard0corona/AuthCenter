import { ApiError } from "../api/client";
import { errorMessage } from "../api/errors";

interface SaveErrorProps {
  error: unknown;
  /** Loads the record again (discarding local edits) after someone else changed it. */
  onReload?: (() => void) | undefined;
  messages?: Record<string, string> | undefined;
}

/** A failed save; a stale version (409) offers to load the current one instead of retrying blindly. */
export function SaveError({ error, onReload, messages }: SaveErrorProps) {
  if (!error) return null;
  const conflict = error instanceof ApiError && error.code === "CONCURRENCY_CONFLICT";
  return (
    <div className="alert alert--error save-error" role="alert">
      <p>{conflict ? "Alguien más cambió este registro desde que lo abriste. Carga la versión actual y vuelve a aplicar tus cambios." : errorMessage(error, messages)}</p>
      {conflict && onReload ? <button className="button button--small button--secondary" type="button" onClick={onReload}>Cargar la versión actual</button> : null}
    </div>
  );
}
