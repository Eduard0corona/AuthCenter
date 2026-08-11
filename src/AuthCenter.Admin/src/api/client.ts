import type { ApiEnvelope } from "./types";

let csrfToken = "";
let redirectStarted = false;

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
  const method = (init.method ?? "GET").toUpperCase();
  const headers = new Headers(init.headers);
  if (init.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  if (!isSafeMethod(method) && csrfToken) headers.set("X-AuthCenter-CSRF", csrfToken);

  const response = await fetch(path, { ...init, method, headers, credentials: "same-origin" });
  const envelope = await readEnvelope<T>(response);
  if (response.status === 401) redirectToLoginOnce();
  if (!response.ok || envelope?.success === false) {
    throw new ApiError(
      response.status,
      envelope?.errorCode ?? "REQUEST_FAILED",
      envelope?.message ?? fallbackMessage(response.status),
      envelope?.details ?? [],
      response.headers.get("x-correlation-id") ?? response.headers.get("x-trace-id")
    );
  }

  if (envelope && "data" in envelope) return envelope.data as T;
  return undefined as T;
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

function redirectToLoginOnce(): void {
  if (redirectStarted || typeof window === "undefined") return;
  redirectStarted = true;
  const returnUrl = `${window.location.pathname}${window.location.search}${window.location.hash}`;
  window.location.replace(`/login?return_url=${encodeURIComponent(returnUrl)}`);
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
