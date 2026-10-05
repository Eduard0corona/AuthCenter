import { z } from "zod";
import type { FederationProvider, FederationRoutingRule, ProfileAttributeDefinition } from "../../api/types";
import { convertExpectedValue } from "../group-rules/group-rule";

export const federationProtocols = ["Oidc", "Saml2"] as const;
export const accountLinkingModes = ["Disabled", "VerifiedEmail"] as const;

const optionalHttpsUrl = (message: string) => z.string().trim().refine((value) => {
  if (!value) return true;
  try { return new URL(value).protocol === "https:"; } catch { return false; }
}, message);

export const federationProviderSchema = z.object({
  applicationSystemId: z.string().uuid("Selecciona una aplicación válida."),
  name: z.string().trim().min(1, "El nombre es obligatorio.").max(150, "Usa máximo 150 caracteres."),
  protocol: z.enum(federationProtocols),
  // OIDC issuers are HTTPS URLs; SAML entity IDs may be any absolute URI (https, http or urn).
  issuer: z.string().trim().min(1, "El emisor (issuer) es obligatorio.").max(500, "Usa máximo 500 caracteres."),
  discoveryEndpoint: optionalHttpsUrl("La URL de descubrimiento debe ser HTTPS."),
  clientId: z.string().trim(),
  oidcCallbackUrl: z.string().trim(),
  clientSecret: z.string(),
  samlSingleSignOnUrl: z.string().trim(),
  samlSigningCertificatePem: z.string().trim(),
  jitProvisioningEnabled: z.boolean(),
  accountLinkingMode: z.enum(accountLinkingModes),
  requireVerifiedEmail: z.boolean(),
  trustUpstreamMfa: z.boolean(),
  groupsClaim: z.string().trim().max(256, "Usa máximo 256 caracteres."),
  groupMappings: z.array(z.object({
    upstreamValue: z.string().trim().min(1, "Indica el valor que envía el IdP.").max(256, "Usa máximo 256 caracteres."),
    directoryGroupId: z.string().uuid("Selecciona un grupo.")
  })).max(200, "Usa máximo 200 mapeos."),
  isActive: z.boolean(),
  /** True while editing an existing SAML provider: a blank PEM keeps the stored certificate. */
  hasStoredCertificate: z.boolean()
}).superRefine((values, context) => {
  if (values.protocol === "Oidc") {
    if (!isHttps(values.issuer)) context.addIssue({ code: "custom", path: ["issuer"], message: "El emisor OIDC debe ser una URL HTTPS." });
    if (!values.clientId) context.addIssue({ code: "custom", path: ["clientId"], message: "El client ID es obligatorio para OIDC." });
    // Blank uses AuthCenter's hosted callback; any other value must be exact and HTTPS.
    if (values.oidcCallbackUrl && !isHttps(values.oidcCallbackUrl)) context.addIssue({ code: "custom", path: ["oidcCallbackUrl"], message: "La URL de retorno debe ser HTTPS y exacta." });
  } else {
    if (!isAbsoluteUri(values.issuer)) context.addIssue({ code: "custom", path: ["issuer"], message: "El entity ID del IdP debe ser una URI absoluta (https:, http: o urn:)." });
    if (!isHttps(values.samlSingleSignOnUrl)) context.addIssue({ code: "custom", path: ["samlSingleSignOnUrl"], message: "La URL de SSO debe ser HTTPS." });
    if (!values.samlSigningCertificatePem && !values.hasStoredCertificate) context.addIssue({ code: "custom", path: ["samlSigningCertificatePem"], message: "Pega el certificado de firma en formato PEM." });
    if (values.samlSigningCertificatePem && !/-----BEGIN CERTIFICATE-----[\s\S]+-----END CERTIFICATE-----/.test(values.samlSigningCertificatePem)) context.addIssue({ code: "custom", path: ["samlSigningCertificatePem"], message: "El certificado debe estar en formato PEM (BEGIN/END CERTIFICATE)." });
  }
  if (values.groupMappings.length > 0 && !values.groupsClaim) context.addIssue({ code: "custom", path: ["groupsClaim"], message: "Indica el claim o atributo de grupos antes de mapear sus valores." });
  const keys = values.groupMappings.map((mapping) => `${mapping.upstreamValue.trim().toLowerCase()}|${mapping.directoryGroupId}`);
  if (new Set(keys).size !== keys.length) context.addIssue({ code: "custom", path: ["groupMappings"], message: "No repitas mapeos de grupo." });
});

export type FederationProviderFormValues = z.input<typeof federationProviderSchema>;

export function federationProviderDefaults(): FederationProviderFormValues {
  return { applicationSystemId: "", name: "", protocol: "Oidc", issuer: "", discoveryEndpoint: "", clientId: "", oidcCallbackUrl: "", clientSecret: "", samlSingleSignOnUrl: "", samlSigningCertificatePem: "", jitProvisioningEnabled: false, accountLinkingMode: "Disabled", requireVerifiedEmail: true, trustUpstreamMfa: false, groupsClaim: "", groupMappings: [], isActive: true, hasStoredCertificate: false };
}

export function federationProviderFromResponse(provider: FederationProvider): FederationProviderFormValues {
  return {
    applicationSystemId: provider.applicationSystemId,
    name: provider.name,
    protocol: provider.protocol,
    issuer: provider.issuer,
    discoveryEndpoint: provider.discoveryEndpoint ?? "",
    clientId: provider.clientId ?? "",
    oidcCallbackUrl: provider.oidcCallbackUrl ?? "",
    clientSecret: "",
    samlSingleSignOnUrl: provider.samlSingleSignOnUrl ?? "",
    samlSigningCertificatePem: "",
    jitProvisioningEnabled: provider.jitProvisioningEnabled,
    accountLinkingMode: provider.accountLinkingMode,
    requireVerifiedEmail: provider.requireVerifiedEmail ?? true,
    trustUpstreamMfa: provider.trustUpstreamMfa ?? false,
    groupsClaim: provider.groupsClaim ?? "",
    groupMappings: (provider.groupMappings ?? []).map((mapping) => ({ upstreamValue: mapping.upstreamValue, directoryGroupId: mapping.directoryGroupId })),
    isActive: provider.isActive,
    hasStoredCertificate: Boolean(provider.samlSigningCertificateThumbprint)
  };
}

/**
 * Body for POST/PUT /api/federation/providers. Fields that belong to the other protocol are cleared so a
 * provider never carries stale OIDC or SAML settings; blank secret/PEM on update keep the stored values.
 */
export function federationProviderPayload(values: FederationProviderFormValues, version = 0) {
  const parsed = federationProviderSchema.parse(values);
  const oidc = parsed.protocol === "Oidc";
  return {
    applicationSystemId: parsed.applicationSystemId,
    name: parsed.name,
    protocol: parsed.protocol,
    issuer: parsed.issuer,
    discoveryEndpoint: oidc && parsed.discoveryEndpoint ? parsed.discoveryEndpoint : null,
    clientId: oidc ? parsed.clientId : null,
    oidcCallbackUrl: oidc && parsed.oidcCallbackUrl ? parsed.oidcCallbackUrl : null,
    clientSecret: oidc && parsed.clientSecret ? parsed.clientSecret : null,
    samlSingleSignOnUrl: oidc ? null : parsed.samlSingleSignOnUrl,
    samlSigningCertificatePem: !oidc && parsed.samlSigningCertificatePem ? parsed.samlSigningCertificatePem : null,
    jitProvisioningEnabled: parsed.jitProvisioningEnabled,
    accountLinkingMode: parsed.accountLinkingMode,
    requireVerifiedEmail: oidc ? parsed.requireVerifiedEmail : true,
    trustUpstreamMfa: parsed.trustUpstreamMfa,
    groupsClaim: parsed.groupsClaim || null,
    groupMappings: parsed.groupMappings.map((mapping) => ({ upstreamValue: mapping.upstreamValue, directoryGroupId: mapping.directoryGroupId })),
    isActive: parsed.isActive,
    version
  };
}

/** Friendly names of the connection-test checks returned by the API. */
export const connectionCheckLabels: Record<string, string> = {
  "provider.active": "Proveedor activo",
  "routing.domain": "Regla por dominio",
  "oidc.discovery": "Documento de descubrimiento (discovery)",
  "oidc.issuer": "Emisor (issuer)",
  "oidc.endpoints": "Endpoints HTTPS",
  "oidc.signing_keys": "Llaves de firma",
  "oidc.response_type": "Código de autorización",
  "oidc.pkce": "PKCE S256",
  "oidc.callback": "URL de retorno hospedada",
  "oidc.client_secret": "Secreto del cliente",
  "oidc.email_scope": "Permiso email (scope)",
  "saml.idp_certificate": "Certificado del IdP",
  "saml.idp_key": "Llave del IdP",
  "saml.sso_url": "URL de SSO",
  "saml.sp_certificate": "Certificado de AuthCenter",
  "saml.sp_endpoints": "Entity ID y ACS de AuthCenter"
};

export const routingRuleSchema = z.object({
  federationProviderId: z.string().uuid("Selecciona un proveedor válido."),
  priority: z.number({ error: "Indica una prioridad numérica." }).int("Usa un entero.").min(1, "La prioridad mínima es 1.").max(10000, "La prioridad máxima es 10000."),
  emailDomain: z.string().trim().toLowerCase().refine((value) => !value || (/^[a-z0-9.-]+\.[a-z0-9-]+$/.test(value) && !value.includes("@")), "Indica un dominio sin @, por ejemplo empresa.com."),
  directoryGroupId: z.string(),
  profileAttributeDefinitionId: z.string(),
  expectedValue: z.string().trim(),
  isActive: z.boolean()
}).superRefine((values, context) => {
  if (!values.emailDomain && !values.directoryGroupId && !values.profileAttributeDefinitionId) context.addIssue({ code: "custom", path: ["emailDomain"], message: "Define al menos una condición: dominio, grupo o atributo." });
  if (values.profileAttributeDefinitionId && !values.expectedValue) context.addIssue({ code: "custom", path: ["expectedValue"], message: "Indica el valor esperado del atributo." });
});

export type RoutingRuleFormValues = z.input<typeof routingRuleSchema>;

export function routingRuleDefaults(rule: FederationRoutingRule | undefined, nextPriority: number): RoutingRuleFormValues {
  if (!rule) return { federationProviderId: "", priority: nextPriority, emailDomain: "", directoryGroupId: "", profileAttributeDefinitionId: "", expectedValue: "", isActive: true };
  return {
    federationProviderId: rule.federationProviderId,
    priority: rule.priority,
    emailDomain: rule.emailDomain ?? "",
    directoryGroupId: rule.directoryGroupId ?? "",
    profileAttributeDefinitionId: rule.profileAttributeDefinitionId ?? "",
    expectedValue: expectedProfileValueToInput(rule.expectedProfileValueJson),
    isActive: rule.isActive
  };
}

export function expectedProfileValueToInput(json: string | null): string {
  if (!json) return "";
  try {
    const value: unknown = JSON.parse(json);
    return value === null || value === undefined ? "" : typeof value === "object" ? json : String(value);
  } catch {
    return json;
  }
}

export type RoutingRulePayloadResult = { ok: true; payload: Record<string, unknown> } | { ok: false; error: string };

/**
 * Builds the create/update body; the expected profile value is serialised with the attribute's JSON type.
 * When the operator cannot read the schema (no definition), an unchanged condition keeps the stored JSON
 * verbatim and a changed value is refused, so a partial-permission edit can never retype the condition.
 */
export function routingRulePayload(values: RoutingRuleFormValues, definition: ProfileAttributeDefinition | undefined, original?: FederationRoutingRule): RoutingRulePayloadResult {
  const parsed = routingRuleSchema.parse(values);
  let expectedProfileValueJson: string | null = null;
  if (parsed.profileAttributeDefinitionId) {
    const unchanged = original !== undefined && original.profileAttributeDefinitionId === parsed.profileAttributeDefinitionId && parsed.expectedValue === expectedProfileValueToInput(original.expectedProfileValueJson);
    if (definition) {
      const converted = convertExpectedValue(parsed.expectedValue, definition.dataType);
      if (!converted.ok) return { ok: false, error: converted.error };
      expectedProfileValueJson = JSON.stringify(converted.value);
    } else if (unchanged) {
      expectedProfileValueJson = original.expectedProfileValueJson;
    } else {
      return { ok: false, error: "No puedes definir el valor de un atributo cuyo esquema no puedes consultar (AUTHCENTER_PROFILE_SCHEMAS_READ)." };
    }
  }
  const body: Record<string, unknown> = {
    priority: parsed.priority,
    emailDomain: parsed.emailDomain || null,
    directoryGroupId: parsed.directoryGroupId || null,
    profileAttributeDefinitionId: parsed.profileAttributeDefinitionId || null,
    expectedProfileValueJson,
    isActive: parsed.isActive
  };
  if (original === undefined) body.federationProviderId = parsed.federationProviderId; else body.version = original.version;
  return { ok: true, payload: body };
}

export function moveRule(rules: FederationRoutingRule[], id: string, direction: -1 | 1): FederationRoutingRule[] {
  const index = rules.findIndex((rule) => rule.id === id);
  const target = index + direction;
  if (index < 0 || target < 0 || target >= rules.length) return rules;
  const next = [...rules];
  [next[index], next[target]] = [next[target]!, next[index]!];
  return next;
}

/** Priorities are re-issued in steps of 10 following the on-screen order, keeping them unique per provider. */
export function reorderPayload(rules: FederationRoutingRule[]) {
  return { rules: rules.map((rule, index) => ({ id: rule.id, priority: (index + 1) * 10, version: rule.version })) };
}

export function orderChanged(original: FederationRoutingRule[], current: FederationRoutingRule[]): boolean {
  return original.length !== current.length || original.some((rule, index) => rule.id !== current[index]?.id);
}

export function describeRoutingRule(rule: FederationRoutingRule, groupName?: string, attributeName?: string): string {
  const parts: string[] = [];
  if (rule.emailDomain) parts.push(`dominio ${rule.emailDomain}`);
  if (rule.directoryGroupId) parts.push(`grupo ${groupName ?? rule.directoryGroupId}`);
  if (rule.profileAttributeDefinitionId) parts.push(`${attributeName ?? rule.profileAttributeDefinitionId} = ${rule.expectedProfileValueJson ?? "?"}`);
  return parts.join(" y ") || "sin condiciones";
}

function isHttps(value: string): boolean {
  try { return new URL(value).protocol === "https:"; } catch { return false; }
}

function isAbsoluteUri(value: string): boolean {
  try { return new URL(value).protocol.length > 1; } catch { return false; }
}
