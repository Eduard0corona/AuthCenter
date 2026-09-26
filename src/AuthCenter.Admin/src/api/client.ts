import type { ApiEnvelope } from "./types";

let csrfToken = "";
let redirectStarted = false;

/** The console administers AuthCenter itself, so it needs a session issued for this application. */
export const ADMIN_APPLICATION_CODE = "AUTHCENTER";

export type ApiErrorKind = "authentication" | "authorization" | "validation" | "conflict" | "transient" | "unexpected";

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly kind: ApiErrorKind;
  readonly details: string[];
  readonly traceId: string | null;

  constructor(status: number, code: string, message: string, details: string[] = [], traceId: string | null = null) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.kind = classifyError(status);
    this.details = details;
    this.traceId = traceId;
  }
}

export function setCsrfToken(value: string): void {
  csrfToken = value;
}

export function getCsrfTokenForTests(): string {
  return csrfToken;
}

export async function apiRequest<T>(path: string, init: RequestInit = {}): Promise<T> {
  // A creation sent twice (a network retry, the CSRF renewal below) must act once: every POST
  // carries one key, reused by its own retries.
  if ((init.method ?? "GET").toUpperCase() === "POST") {
    const headers = new Headers(init.headers);
    if (!headers.has("Idempotency-Key")) headers.set("Idempotency-Key", crypto.randomUUID());
    init = { ...init, headers };
  }
  return send<T>(path, init, true);
}

async function send<T>(path: string, init: RequestInit, mayRenewCsrf: boolean): Promise<T> {
  const method = (init.method ?? "GET").toUpperCase();
  const headers = new Headers(init.headers);
  if (init.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  if (!isSafeMethod(method) && csrfToken) headers.set("X-AuthCenter-CSRF", csrfToken);

  const response = await fetch(path, { ...init, method, headers, credentials: "same-origin" });
  const envelope = await readEnvelope<T>(response);
  if (response.status === 401) redirectToLoginOnce();
  // The double-submit cookie is replaced whenever another tab (the portal, another console) reads
  // the session, and expires with it. Renew it once and repeat the request.
  if (mayRenewCsrf && !isSafeMethod(method) && response.status === 400 && envelope?.errorCode === "INVALID_CSRF_TOKEN" && await renewCsrfToken())
    return send<T>(path, init, false);
  if (!response.ok || envelope?.success === false) {
    throw new ApiError(
      response.status,
      envelope?.errorCode ?? "REQUEST_FAILED",
      envelope?.message ?? fallbackMessage(response.status),
      envelope?.details ?? [],
      envelope?.traceId ?? response.headers.get("x-correlation-id") ?? response.headers.get("x-trace-id")
    );
  }

  if (envelope && "data" in envelope) return envelope.data as T;
  return undefined as T;
}

let renewal: Promise<boolean> | null = null;

/** Reads the session again for a fresh CSRF token; concurrent failures share one renewal. */
function renewCsrfToken(): Promise<boolean> {
  renewal ??= (async () => {
    try {
      const response = await fetch("/ui-api/session", { credentials: "same-origin", headers: { Accept: "application/json" } });
      const token = response.ok ? (await readEnvelope<{ csrfToken?: string }>(response))?.data?.csrfToken : undefined;
      if (!token) return false;
      csrfToken = token;
      return true;
    } catch {
      return false;
    } finally {
      renewal = null;
    }
  })();
  return renewal;
}

function isSafeMethod(method: string): boolean {
  return method === "GET" || method === "HEAD" || method === "OPTIONS";
}

async function readEnvelope<T>(response: Response): Promise<ApiEnvelope<T> | null> {
  const contentType = response.headers.get("content-type") ?? "";
  if (!contentType.includes("json")) return null;
  const text = await response.text();
  if (!text) return null;
  try {
    return JSON.parse(text) as ApiEnvelope<T>;
  } catch {
    return null;
  }
}

export function redirectToLoginOnce(): void {
  if (redirectStarted || typeof window === "undefined") return;
  redirectStarted = true;
  const returnUrl = `${window.location.pathname}${window.location.search}${window.location.hash}`;
  window.location.replace(`/login?application=${ADMIN_APPLICATION_CODE}&return_url=${encodeURIComponent(returnUrl)}`);
}

function classifyError(status: number): ApiErrorKind {
  if (status === 401) return "authentication";
  if (status === 403) return "authorization";
  if (status === 409 || status === 412) return "conflict";
  if (status === 400 || status === 422) return "validation";
  if (status === 429 || status >= 500) return "transient";
  return "unexpected";
}

function fallbackMessage(status: number): string {
  if (status === 401) return "Tu sesión expiró. Te redirigiremos para iniciar sesión.";
  if (status === 403) return "No tienes permiso para realizar esta operación.";
  if (status === 409 || status === 412) return "El recurso cambió. Actualiza la página e inténtalo de nuevo.";
  if (status === 429) return "Hay demasiadas solicitudes. Espera un momento e inténtalo de nuevo.";
  if (status >= 500) return "AuthCenter no está disponible temporalmente.";
  return `La solicitud no pudo completarse (${status}).`;
}
