import { z } from "zod";
import type { OAuthClientSummary } from "../../api/types";

export const oauthScopes = ["openid", "profile", "email", "offline_access"] as const;
export const tokenExchangeGrant = "urn:ietf:params:oauth:grant-type:token-exchange";
export const oauthGrants = ["authorization_code", "client_credentials", "refresh_token", tokenExchangeGrant] as const;

// What each grant and OIDC scope is for; the console shows the protocol name next to it.
export const grantLabels: Record<(typeof oauthGrants)[number], string> = {
  authorization_code: "Código de autorización",
  client_credentials: "Credenciales del cliente (servicio a servicio)",
  refresh_token: "Token de actualización",
  [tokenExchangeGrant]: "Intercambio de tokens"
};
export const scopeLabels: Record<(typeof oauthScopes)[number], string> = {
  openid: "Identidad de quien inicia sesión",
  profile: "Nombre y foto de perfil",
  email: "Dirección de correo",
  offline_access: "Mantener el acceso (token de actualización)"
};

/** A grant's name for people; an unknown grant keeps its protocol name. */
export function grantLabel(grant: string): string {
  return grantLabels[grant as (typeof oauthGrants)[number]] ?? grant;
}

/** An empty required field; a value in the wrong format gets its own message. */
export const REQUIRED = "Este campo es obligatorio.";
const URL_FORMAT = "Usa una URL HTTPS (o HTTP en localhost), sin fragmentos ni credenciales.";
const clientIdPattern = /^[a-z0-9\-_]+$/;

/**
 * The Client ID suggested for a display name: lowercase ASCII words joined by hyphens, accents
 * dropped ("Portal de Socios Ñandú" → "portal-de-socios-nandu"). Empty when nothing is left.
 */
export function suggestClientId(displayName: string): string {
  return displayName
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 100)
    .replace(/-+$/, "");
}

/** AuthCenter's hosted login: the Login URL of every client, unless AuthCenter moves to another domain. */
export function hostedLoginUrl(origin: string): string {
  return `${origin.replace(/\/+$/, "")}/login`;
}
// API scopes come from the API catalog; the server checks that each one exists.
const apiScopePattern = /^[a-z][a-z0-9_.:-]{1,127}$/;
const isOidcScope = (scope: string): scope is (typeof oauthScopes)[number] => (oauthScopes as readonly string[]).includes(scope);

const secureBrowserUrl = (value: string): boolean => {
  try {
    const url = new URL(value);
    if (url.username || url.password || url.hash) return false;
    if (url.protocol === "https:") return true;
    return url.protocol === "http:" && (url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]");
  } catch {
    return false;
  }
};

// An origin is scheme://host[:port] and nothing else.
const isOrigin = (value: string): boolean => {
  if (!secureBrowserUrl(value)) return false;
  try {
    return new URL(value).origin === value.replace(/\/$/, "").toLowerCase();
  } catch {
    return false;
  }
};

const uriLines = z.string().transform((value) => value.split(/\r?\n/).map((item) => item.trim()).filter(Boolean));

export const oauthClientSchema = z.object({
  applicationSystemId: z.string().min(1, REQUIRED).uuid("Selecciona una aplicación válida."),
  clientId: z.string().trim().min(1, REQUIRED).max(100, "Usa máximo 100 caracteres.")
    .regex(clientIdPattern, "Usa minúsculas, números, guiones o guion bajo."),
  displayName: z.string().trim().min(1, REQUIRED).max(200, "Usa máximo 200 caracteres."),
  clientType: z.enum(["0", "1"]),
  redirectUris: z.string(),
  allowedScopes: z.array(z.enum(oauthScopes)),
  apiScopes: z.string(),
  grantTypes: z.array(z.enum(oauthGrants)).min(1, "Selecciona al menos un flujo."),
  loginUrl: z.string().trim().min(1, REQUIRED).refine((value) => !value || secureBrowserUrl(value), URL_FORMAT),
  allowedCorsOrigins: z.string(),
  postLogoutRedirectUris: z.string(),
  backchannelLogoutUri: z.string().trim(),
  // Optional so a form built before the field existed still parses; the default keeps the server's.
  backchannelLogoutSessionRequired: z.boolean().optional(),
  accessTokenLifetimeSeconds: z.coerce.number().int().min(60, "El mínimo es 60 segundos.").max(3600, "El máximo es 3600 segundos."),
  requirePkce: z.boolean(),
  autoConsent: z.boolean(),
  isActive: z.boolean()
}).superRefine((values, context) => {
  const apiScopes = uriLines.parse(values.apiScopes);
  if (values.allowedScopes.length === 0 && apiScopes.length === 0) {
    context.addIssue({ code: "custom", path: ["allowedScopes"], message: "Elige al menos un dato o un permiso de API que pueda pedir." });
  }
  if (apiScopes.some((scope) => !apiScopePattern.test(scope) || isOidcScope(scope))) {
    context.addIssue({ code: "custom", path: ["apiScopes"], message: "Usa nombres del catálogo de APIs: minúsculas, dígitos, '.', '_', ':' o '-'." });
  }
  if (new Set(apiScopes).size !== apiScopes.length) {
    context.addIssue({ code: "custom", path: ["apiScopes"], message: "No repitas scopes." });
  }
  if (values.clientType === "1" && values.grantTypes.includes(tokenExchangeGrant)) {
    context.addIssue({ code: "custom", path: ["grantTypes"], message: "Un cliente público no puede usar el intercambio de tokens." });
  }
  const redirects = uriLines.parse(values.redirectUris);
  if (values.grantTypes.includes("authorization_code") && redirects.length === 0) {
    context.addIssue({ code: "custom", path: ["redirectUris"], message: "Este campo es obligatorio para el flujo de código de autorización." });
  }
  if (redirects.some((uri) => !secureBrowserUrl(uri))) {
    context.addIssue({ code: "custom", path: ["redirectUris"], message: "Cada URL debe usar HTTPS (o HTTP en localhost), sin fragmentos ni credenciales." });
  }
  if (new Set(redirects).size !== redirects.length) {
    context.addIssue({ code: "custom", path: ["redirectUris"], message: "No repitas URLs de regreso." });
  }
  const corsOrigins = uriLines.parse(values.allowedCorsOrigins);
  if (corsOrigins.some((origin) => !isOrigin(origin))) {
    context.addIssue({ code: "custom", path: ["allowedCorsOrigins"], message: "Usa sólo esquema y host (https://app.example.com), con HTTPS o HTTP en localhost." });
  }
  if (new Set(corsOrigins).size !== corsOrigins.length) {
    context.addIssue({ code: "custom", path: ["allowedCorsOrigins"], message: "No repitas orígenes." });
  }
  const postLogout = uriLines.parse(values.postLogoutRedirectUris);
  if (postLogout.some((uri) => !secureBrowserUrl(uri))) {
    context.addIssue({ code: "custom", path: ["postLogoutRedirectUris"], message: "Cada URL debe usar HTTPS (o HTTP en localhost), sin fragmentos ni credenciales." });
  }
  if (new Set(postLogout).size !== postLogout.length) {
    context.addIssue({ code: "custom", path: ["postLogoutRedirectUris"], message: "No repitas URLs." });
  }
  if (values.backchannelLogoutUri && !secureBrowserUrl(values.backchannelLogoutUri)) {
    context.addIssue({ code: "custom", path: ["backchannelLogoutUri"], message: URL_FORMAT });
  }
  if (values.backchannelLogoutUri && !values.grantTypes.includes("authorization_code")) {
    context.addIssue({ code: "custom", path: ["backchannelLogoutUri"], message: "El aviso de cierre de sesión sólo aplica al flujo de código de autorización." });
  }
  if (values.grantTypes.includes("authorization_code") && !values.requirePkce) {
    context.addIssue({ code: "custom", path: ["requirePkce"], message: "El flujo de código de autorización requiere PKCE." });
  }
  if (values.clientType === "1" && values.grantTypes.includes("client_credentials")) {
    context.addIssue({ code: "custom", path: ["grantTypes"], message: "Un cliente público no puede usar credenciales del cliente." });
  }
  if (values.grantTypes.includes("refresh_token") && !values.grantTypes.includes("authorization_code")) {
    context.addIssue({ code: "custom", path: ["grantTypes"], message: "El token de actualización requiere el flujo de código de autorización." });
  }
  if (values.allowedScopes.includes("offline_access") && (!values.grantTypes.includes("authorization_code") || !values.grantTypes.includes("refresh_token"))) {
    context.addIssue({ code: "custom", path: ["allowedScopes"], message: "offline_access requiere los flujos de código de autorización y de token de actualización." });
  }
  if (values.grantTypes.length === 1 && values.grantTypes[0] === "client_credentials" && (values.allowedScopes.includes("openid") || values.allowedScopes.includes("offline_access"))) {
    context.addIssue({ code: "custom", path: ["allowedScopes"], message: "Un cliente de servicio a servicio no puede pedir openid ni offline_access." });
  }
});

export type OAuthClientFormValues = z.input<typeof oauthClientSchema>;

export function oauthClientDefaults(client?: OAuthClientSummary): OAuthClientFormValues {
  return {
    applicationSystemId: client?.applicationSystemId ?? "",
    clientId: client?.clientId ?? "",
    displayName: client?.displayName ?? "",
    clientType: String(client?.clientType ?? 0) as "0" | "1",
    redirectUris: client?.redirectUris.join("\n") ?? "",
    // offline_access with refresh tokens: what the .NET BFF SDK asks for out of the box.
    allowedScopes: client ? client.allowedScopes.filter(isOidcScope) : ["openid", "profile", "email", "offline_access"],
    apiScopes: client?.allowedScopes.filter((scope) => !isOidcScope(scope)).join("\n") ?? "",
    grantTypes: client?.grantTypes as OAuthClientFormValues["grantTypes"] ?? ["authorization_code", "refresh_token"],
    loginUrl: client?.loginUrl ?? "",
    allowedCorsOrigins: client?.allowedCorsOrigins?.join("\n") ?? "",
    postLogoutRedirectUris: client?.postLogoutRedirectUris?.join("\n") ?? "",
    backchannelLogoutUri: client?.backchannelLogoutUri ?? "",
    backchannelLogoutSessionRequired: client?.backchannelLogoutSessionRequired ?? true,
    accessTokenLifetimeSeconds: client?.accessTokenLifetimeSeconds ?? 900,
    requirePkce: client?.requirePkce ?? true,
    autoConsent: client?.autoConsent ?? false,
    isActive: client?.isActive ?? true
  };
}

/** A new client: the hosted login prefilled and, when the link names one, its application. */
export function newOAuthClientDefaults(origin: string, applicationSystemId = ""): OAuthClientFormValues {
  return { ...oauthClientDefaults(), applicationSystemId, loginUrl: hostedLoginUrl(origin) };
}

/** The API scopes of the form, one per line. */
export function splitApiScopes(value: string): string[] {
  return uriLines.parse(value);
}

export function oauthClientPayload(values: OAuthClientFormValues, create: boolean) {
  const parsed = oauthClientSchema.parse(values);
  return {
    ...(create ? {
      applicationSystemId: parsed.applicationSystemId,
      clientId: parsed.clientId,
      clientType: Number(parsed.clientType)
    } : {}),
    displayName: parsed.displayName,
    redirectUris: uriLines.parse(parsed.redirectUris),
    allowedScopes: [...parsed.allowedScopes, ...uriLines.parse(parsed.apiScopes)],
    grantTypes: parsed.grantTypes,
    loginUrl: parsed.loginUrl,
    allowedCorsOrigins: uriLines.parse(parsed.allowedCorsOrigins),
    postLogoutRedirectUris: uriLines.parse(parsed.postLogoutRedirectUris),
    backchannelLogoutUri: parsed.backchannelLogoutUri || null,
    // Sent explicitly: omitting it made every console save turn the setting back on.
    backchannelLogoutSessionRequired: parsed.backchannelLogoutSessionRequired ?? true,
    accessTokenLifetimeSeconds: parsed.accessTokenLifetimeSeconds,
    requirePkce: parsed.requirePkce,
    autoConsent: parsed.autoConsent,
    ...(create ? {} : { isActive: parsed.isActive })
  };
}
