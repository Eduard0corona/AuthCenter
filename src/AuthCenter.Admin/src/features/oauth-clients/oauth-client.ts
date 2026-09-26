import { z } from "zod";
import type { OAuthClientSummary } from "../../api/types";

export const oauthScopes = ["openid", "profile", "email", "offline_access"] as const;
export const tokenExchangeGrant = "urn:ietf:params:oauth:grant-type:token-exchange";
export const oauthGrants = ["authorization_code", "client_credentials", "refresh_token", tokenExchangeGrant] as const;
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
  applicationSystemId: z.string().uuid("Selecciona una aplicación válida."),
  clientId: z.string().trim().min(1, "El client ID es obligatorio.").max(100, "Usa máximo 100 caracteres.")
    .regex(/^[a-z0-9\-_]+$/, "Usa minúsculas, números, guiones o guion bajo."),
  displayName: z.string().trim().min(1, "El nombre es obligatorio.").max(200, "Usa máximo 200 caracteres."),
  clientType: z.enum(["0", "1"]),
  redirectUris: z.string(),
  allowedScopes: z.array(z.enum(oauthScopes)),
  apiScopes: z.string(),
  grantTypes: z.array(z.enum(oauthGrants)).min(1, "Selecciona al menos un grant."),
  loginUrl: z.string().trim().refine(secureBrowserUrl, "Usa HTTPS o HTTP loopback, sin fragmentos ni credenciales."),
  allowedCorsOrigins: z.string(),
  postLogoutRedirectUris: z.string(),
  backchannelLogoutUri: z.string().trim(),
  accessTokenLifetimeSeconds: z.coerce.number().int().min(60, "El mínimo es 60 segundos.").max(3600, "El máximo es 3600 segundos."),
  requirePkce: z.boolean(),
  autoConsent: z.boolean(),
  isActive: z.boolean()
}).superRefine((values, context) => {
  const apiScopes = uriLines.parse(values.apiScopes);
  if (values.allowedScopes.length === 0 && apiScopes.length === 0) {
    context.addIssue({ code: "custom", path: ["allowedScopes"], message: "Selecciona al menos un scope." });
  }
  if (apiScopes.some((scope) => !apiScopePattern.test(scope) || isOidcScope(scope))) {
    context.addIssue({ code: "custom", path: ["apiScopes"], message: "Usa nombres del catálogo de APIs: minúsculas, dígitos, '.', '_', ':' o '-'." });
  }
  if (new Set(apiScopes).size !== apiScopes.length) {
    context.addIssue({ code: "custom", path: ["apiScopes"], message: "No repitas scopes." });
  }
  if (values.clientType === "1" && values.grantTypes.includes(tokenExchangeGrant)) {
    context.addIssue({ code: "custom", path: ["grantTypes"], message: "Un cliente público no puede usar token exchange." });
  }
  const redirects = uriLines.parse(values.redirectUris);
  if (values.grantTypes.includes("authorization_code") && redirects.length === 0) {
    context.addIssue({ code: "custom", path: ["redirectUris"], message: "Authorization code requiere al menos un redirect URI." });
  }
  if (redirects.some((uri) => !secureBrowserUrl(uri))) {
    context.addIssue({ code: "custom", path: ["redirectUris"], message: "Cada URI debe usar HTTPS o HTTP loopback, sin fragmentos ni credenciales." });
  }
  if (new Set(redirects).size !== redirects.length) {
    context.addIssue({ code: "custom", path: ["redirectUris"], message: "No repitas redirect URIs." });
  }
  const corsOrigins = uriLines.parse(values.allowedCorsOrigins);
  if (corsOrigins.some((origin) => !isOrigin(origin))) {
    context.addIssue({ code: "custom", path: ["allowedCorsOrigins"], message: "Usa sólo esquema y host (https://app.example.com), con HTTPS o HTTP loopback." });
  }
  if (new Set(corsOrigins).size !== corsOrigins.length) {
    context.addIssue({ code: "custom", path: ["allowedCorsOrigins"], message: "No repitas orígenes." });
  }
  const postLogout = uriLines.parse(values.postLogoutRedirectUris);
  if (postLogout.some((uri) => !secureBrowserUrl(uri))) {
    context.addIssue({ code: "custom", path: ["postLogoutRedirectUris"], message: "Cada URI debe usar HTTPS o HTTP loopback, sin fragmentos ni credenciales." });
  }
  if (new Set(postLogout).size !== postLogout.length) {
    context.addIssue({ code: "custom", path: ["postLogoutRedirectUris"], message: "No repitas URIs de cierre de sesión." });
  }
  if (values.backchannelLogoutUri && !secureBrowserUrl(values.backchannelLogoutUri)) {
    context.addIssue({ code: "custom", path: ["backchannelLogoutUri"], message: "Usa HTTPS o HTTP loopback, sin fragmentos ni credenciales." });
  }
  if (values.backchannelLogoutUri && !values.grantTypes.includes("authorization_code")) {
    context.addIssue({ code: "custom", path: ["backchannelLogoutUri"], message: "El back-channel logout sólo aplica a clientes con authorization code." });
  }
  if (values.grantTypes.includes("authorization_code") && !values.requirePkce) {
    context.addIssue({ code: "custom", path: ["requirePkce"], message: "Authorization code requiere PKCE." });
  }
  if (values.clientType === "1" && values.grantTypes.includes("client_credentials")) {
    context.addIssue({ code: "custom", path: ["grantTypes"], message: "Un cliente público no puede usar client credentials." });
  }
  if (values.grantTypes.includes("refresh_token") && !values.grantTypes.includes("authorization_code")) {
    context.addIssue({ code: "custom", path: ["grantTypes"], message: "Refresh token requiere authorization code." });
  }
  if (values.allowedScopes.includes("offline_access") && (!values.grantTypes.includes("authorization_code") || !values.grantTypes.includes("refresh_token"))) {
    context.addIssue({ code: "custom", path: ["allowedScopes"], message: "offline_access requiere authorization code y refresh token." });
  }
  if (values.grantTypes.length === 1 && values.grantTypes[0] === "client_credentials" && (values.allowedScopes.includes("openid") || values.allowedScopes.includes("offline_access"))) {
    context.addIssue({ code: "custom", path: ["allowedScopes"], message: "Un cliente machine-to-machine no puede solicitar openid ni offline_access." });
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
    allowedScopes: client ? client.allowedScopes.filter(isOidcScope) : ["openid", "profile", "email"],
    apiScopes: client?.allowedScopes.filter((scope) => !isOidcScope(scope)).join("\n") ?? "",
    grantTypes: client?.grantTypes as OAuthClientFormValues["grantTypes"] ?? ["authorization_code", "refresh_token"],
    loginUrl: client?.loginUrl ?? "",
    allowedCorsOrigins: client?.allowedCorsOrigins?.join("\n") ?? "",
    postLogoutRedirectUris: client?.postLogoutRedirectUris?.join("\n") ?? "",
    backchannelLogoutUri: client?.backchannelLogoutUri ?? "",
    accessTokenLifetimeSeconds: client?.accessTokenLifetimeSeconds ?? 900,
    requirePkce: client?.requirePkce ?? true,
    autoConsent: client?.autoConsent ?? false,
    isActive: client?.isActive ?? true
  };
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
    accessTokenLifetimeSeconds: parsed.accessTokenLifetimeSeconds,
    requirePkce: parsed.requirePkce,
    autoConsent: parsed.autoConsent,
    ...(create ? {} : { isActive: parsed.isActive })
  };
}
