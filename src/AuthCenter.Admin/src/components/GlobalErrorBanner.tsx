import { useEffect, useState } from "react";
import { ApiError } from "../api/client";
import { errorMessage } from "../api/errors";

/**
 * Surfaces failures that happen outside React rendering (event handlers, stray promises). A failed
 * API request is not one of them: the page that sent it shows the error (its mutation or query
 * state), even when a form handler lets the rejection escape.
 */
export function GlobalErrorBanner() {
  const [message, setMessage] = useState("");
  useEffect(() => {
    const report = (error: unknown) => {
      if (error instanceof ApiError || (error instanceof DOMException && error.name === "AbortError")) return;
      setMessage(errorMessage(error));
    };
    const onError = (event: ErrorEvent) => report(event.error ?? new Error(event.message));
    const onRejection = (event: PromiseRejectionEvent) => report(event.reason);
    window.addEventListener("error", onError);
    window.addEventListener("unhandledrejection", onRejection);
    return () => {
      window.removeEventListener("error", onError);
      window.removeEventListener("unhandledrejection", onRejection);
    };
  }, []);
  if (!message) return null;
  return (
    <div className="alert alert--error global-error" role="alert">
      <span>{message}</span>
      <button className="button button--small button--secondary" type="button" onClick={() => setMessage("")}>Cerrar</button>
    </div>
  );
}
