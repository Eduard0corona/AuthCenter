import { z } from "zod";
import type { FederationProvider, FederationRoutingRule, ProfileAttributeDefinition } from "../../api/types";
import { convertExpectedValue } from "../group-rules/group-rule";

export const federationProtocols = ["Oidc", "Saml2"] as const;
export const accountLinkingModes = ["Disabled", "VerifiedEmail"] as const;

const httpsUrl = (message: string) => z.string().trim().refine((value) => {
  try { return new URL(value).protocol === "https:"; } catch { return false; }
}, message);
const optionalHttpsUrl = (message: string) => z.string().trim().refine((value) => {
  if (!value) return true;
  try { return new URL(value).protocol === "https:"; } catch { return false; }
}, message);

export const federationProviderSchema = z.object({
  applicationSystemId: z.string().uuid("Selecciona una aplicación válida."),
  name: z.string().trim().min(1, "El nombre es obligatorio.").max(150, "Usa máximo 150 caracteres."),
  protocol: z.enum(federationProtocols),
  issuer: httpsUrl("El issuer debe ser una URL HTTPS."),
  discoveryEndpoint: optionalHttpsUrl("El discovery endpoint debe ser una URL HTTPS."),
  clientId: z.string().trim(),
  oidcCallbackUrl: z.string().trim(),
  clientSecret: z.string(),
  samlSingleSignOnUrl: z.string().trim(),
  samlSigningCertificatePem: z.string().trim(),
  jitProvisioningEnabled: z.boolean(),
  accountLinkingMode: z.enum(accountLinkingModes),
  isActive: z.boolean(),
  /** True while editing an existing SAML provider: a blank PEM keeps the stored certificate. */
  hasStoredCertificate: z.boolean()
}).superRefine((values, context) => {
  if (values.protocol === "Oidc") {
    if (!values.clientId) context.addIssue({ code: "custom", path: ["clientId"], message: "El client ID es obligatorio para OIDC." });
    if (!isHttps(values.oidcCallbackUrl)) context.addIssue({ code: "custom", path: ["oidcCallbackUrl"], message: "La callback URL debe ser HTTPS y exacta." });
  } else {
    if (!isHttps(values.samlSingleSignOnUrl)) context.addIssue({ code: "custom", path: ["samlSingleSignOnUrl"], message: "La URL de SSO debe ser HTTPS." });
    if (!values.samlSigningCertificatePem && !values.hasStoredCertificate) context.addIssue({ code: "custom", path: ["samlSigningCertificatePem"], message: "Pega el certificado de firma en formato PEM." });
    if (values.samlSigningCertificatePem && !/-----BEGIN CERTIFICATE-----[\s\S]+-----END CERTIFICATE-----/.test(values.samlSigningCertificatePem)) context.addIssue({ code: "custom", path: ["samlSigningCertificatePem"], message: "El certificado debe estar en formato PEM (BEGIN/END CERTIFICATE)." });
  }
});

export type FederationProviderFormValues = z.input<typeof federationProviderSchema>;

export function federationProviderDefaults(): FederationProviderFormValues {
  return { applicationSystemId: "", name: "", protocol: "Oidc", issuer: "", discoveryEndpoint: "", clientId: "", oidcCallbackUrl: "", clientSecret: "", samlSingleSignOnUrl: "", samlSigningCertificatePem: "", jitProvisioningEnabled: false, accountLinkingMode: "Disabled", isActive: true, hasStoredCertificate: false };
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
    oidcCallbackUrl: oidc ? parsed.oidcCallbackUrl : null,
    clientSecret: oidc && parsed.clientSecret ? parsed.clientSecret : null,
    samlSingleSignOnUrl: oidc ? null : parsed.samlSingleSignOnUrl,
    samlSigningCertificatePem: !oidc && parsed.samlSigningCertificatePem ? parsed.samlSigningCertificatePem : null,
    jitProvisioningEnabled: parsed.jitProvisioningEnabled,
    accountLinkingMode: parsed.accountLinkingMode,
    isActive: parsed.isActive,
    version
  };
}

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
