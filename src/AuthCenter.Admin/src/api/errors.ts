import { ApiError } from "./client";

/** Spanish text for the platform's generic error codes; domain errors keep the server's message. */
const GENERIC_MESSAGES: Record<string, string> = {
  VALIDATION_FAILED: "Revisa los datos del formulario.",
  NOT_FOUND: "El recurso no existe o ya fue eliminado.",
  FORBIDDEN: "No tienes permiso para realizar esta operación.",
  UNAUTHORIZED: "Tu sesión expiró. Te redirigiremos para iniciar sesión.",
  CONCURRENCY_CONFLICT: "Alguien más cambió este recurso. Recarga la página para ver la versión actual.",
  REAUTHENTICATION_REQUIRED: "Confirma tu identidad para continuar.",
  INVALID_CSRF_TOKEN: "Tu sesión se renovó. Vuelve a intentarlo.",
  IDEMPOTENCY_KEY_REQUIRED: "La operación necesita una clave de idempotencia. Vuelve a intentarlo.",
  RATE_LIMITED: "Hay demasiadas solicitudes. Espera un momento e inténtalo de nuevo.",
  INTERNAL_ERROR: "AuthCenter no pudo completar la operación."
};

/**
 * The message to show for a failed request: the page's own text for a known error code (else the
 * generic one or the server's), the validation details when there are any, and the trace reference
 * support needs to find the request (except for validation, which the operator fixes on their own).
 */
export function errorMessage(error: unknown, messages: Record<string, string> = {}, fallback = "Ocurrió un error inesperado."): string {
  if (isStaleBuildError(error)) return "La consola se actualizó mientras estaba abierta. Recarga la página para continuar.";
  if (isNetworkError(error)) return "No pudimos conectar con AuthCenter. Revisa tu conexión e inténtalo de nuevo.";
  if (!(error instanceof ApiError)) return error instanceof Error && error.message ? error.message : fallback;
  const base = messages[error.code] ?? GENERIC_MESSAGES[error.code] ?? error.message;
  const details = error.details.filter((detail) => detail && detail !== error.message);
  const text = details.length > 0 ? `${base} ${details.join(" ")}` : base;
  return error.traceId && error.kind !== "validation" ? `${text} Referencia: ${error.traceId}` : text;
}

/** fetch rejects with a TypeError when the network fails (wording differs per browser). */
export function isNetworkError(error: unknown): boolean {
  return error instanceof TypeError && !isStaleBuildError(error) && /fetch|network|load failed/i.test(error.message);
}

/** A lazy chunk that no longer exists: the console was updated while this tab was open. */
export function isStaleBuildError(error: unknown): boolean {
  return error instanceof Error && /dynamically imported module|importing a module script failed|error loading dynamically/i.test(error.message);
}
