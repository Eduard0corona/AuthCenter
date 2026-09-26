import { z } from "zod";
import type { SamlServiceProvider, SamlServiceProviderMetadata } from "../../api/types";

export const nameIdFormats = [
  "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress",
  "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent",
  "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified"
] as const;

export const nameIdFormatLabels: Record<typeof nameIdFormats[number], string> = {
  "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress": "Correo electrónico",
  "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent": "Persistente (opaco, distinto por aplicación)",
  "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified": "Sin especificar (id del usuario)"
};

/** The attribute sources the server knows; profile attributes are added as profile:<key>. */
export const attributeSources: { value: string; label: string }[] = [
  { value: "email", label: "Correo" },
  { value: "name", label: "Nombre completo" },
  { value: "userId", label: "Id del usuario" },
  { value: "roles", label: "Roles en la aplicación" },
  { value: "permissions", label: "Permisos en la aplicación" },
  { value: "groups", label: "Grupos del directorio" }
];

/** Assertions are posted by the browser: HTTPS, or HTTP only on this computer for development. */
export function isBrowserEndpoint(value: string): boolean {
  try {
    const url = new URL(value);
    if (url.hash) return false;
    return url.protocol === "https:" || url.protocol === "http:" && ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
  } catch {
    return false;
  }
}

export function lines(value: string): string[] {
  return [...new Set(value.split("\n").map((line) => line.trim()).filter(Boolean))];
}

const attributeSchema = z.object({
  name: z.string().trim().min(1, "Indica el nombre del atributo.").max(200, "Usa máximo 200 caracteres."),
  source: z.string().min(1, "Elige el origen.")
});

const fields = {
  applicationSystemId: z.string(),
  name: z.string().trim().min(1, "Indica el nombre.").max(150, "Usa máximo 150 caracteres."),
  entityId: z.string().trim().min(1, "Indica el entity ID.").max(500, "Usa máximo 500 caracteres.").regex(/^\S+$/, "El entity ID no lleva espacios."),
  assertionConsumerServiceUrls: z.string().trim().min(1, "Indica al menos una URL de ACS."),
  singleLogoutServiceUrl: z.string().trim().max(1000, "Usa máximo 1000 caracteres."),
  nameIdFormat: z.enum(nameIdFormats),
  signingCertificate: z.string().trim(),
  requireSignedRequests: z.boolean(),
  encryptionCertificate: z.string().trim(),
  encryptAssertions: z.boolean(),
  signResponse: z.boolean(),
  attributes: z.array(attributeSchema).max(30, "Usa máximo 30 atributos."),
  allowIdpInitiated: z.boolean(),
  defaultRelayState: z.string().trim().max(500, "Usa máximo 500 caracteres."),
  assertionLifetimeMinutes: z.coerce.number<number>().int("Usa minutos enteros.").min(1, "El mínimo es 1 minuto.").max(60, "El máximo es 60 minutos."),
  isActive: z.boolean()
};

function refine(values: z.infer<z.ZodObject<typeof fields>>, context: z.RefinementCtx): void {
  const urls = lines(values.assertionConsumerServiceUrls);
  if (urls.length > 10) context.addIssue({ code: "custom", path: ["assertionConsumerServiceUrls"], message: "Registra máximo 10 URLs." });
  const invalid = urls.find((url) => !isBrowserEndpoint(url));
  if (invalid) context.addIssue({ code: "custom", path: ["assertionConsumerServiceUrls"], message: `${invalid} no es una URL HTTPS (HTTP sólo en localhost).` });
  if (values.singleLogoutServiceUrl && !isBrowserEndpoint(values.singleLogoutServiceUrl))
    context.addIssue({ code: "custom", path: ["singleLogoutServiceUrl"], message: "Usa una URL HTTPS (HTTP sólo en localhost)." });
  if (values.requireSignedRequests && !values.signingCertificate)
    context.addIssue({ code: "custom", path: ["signingCertificate"], message: "Exigir firmas necesita el certificado de firma de la aplicación." });
  if (values.encryptAssertions && !values.encryptionCertificate)
    context.addIssue({ code: "custom", path: ["encryptionCertificate"], message: "Cifrar necesita el certificado de cifrado de la aplicación." });
  const seen = new Set<string>();
  values.attributes.forEach((attribute, index) => {
    const key = attribute.name.trim().toLowerCase();
    if (seen.has(key)) context.addIssue({ code: "custom", path: ["attributes", index, "name"], message: "Este atributo ya está en la lista." });
    seen.add(key);
  });
}

export const samlAppSchema = z.object(fields).superRefine(refine);
export const newSamlAppSchema = z.object(fields).superRefine((values, context) => {
  refine(values, context);
  if (!/^[0-9a-f-]{36}$/i.test(values.applicationSystemId))
    context.addIssue({ code: "custom", path: ["applicationSystemId"], message: "Selecciona la aplicación." });
});

export type SamlAppFormValues = z.input<typeof samlAppSchema>;

export function samlAppDefaults(provider?: SamlServiceProvider): SamlAppFormValues {
  return {
    applicationSystemId: provider?.applicationSystemId ?? "",
    name: provider?.name ?? "",
    entityId: provider?.entityId ?? "",
    assertionConsumerServiceUrls: provider?.assertionConsumerServiceUrls.join("\n") ?? "",
    singleLogoutServiceUrl: provider?.singleLogoutServiceUrl ?? "",
    nameIdFormat: (nameIdFormats as readonly string[]).includes(provider?.nameIdFormat ?? "") ? provider!.nameIdFormat as typeof nameIdFormats[number] : nameIdFormats[0],
    signingCertificate: provider?.signingCertificate?.pem ?? "",
    requireSignedRequests: provider?.requireSignedRequests ?? false,
    encryptionCertificate: provider?.encryptionCertificate?.pem ?? "",
    encryptAssertions: provider?.encryptAssertions ?? false,
    signResponse: provider?.signResponse ?? true,
    attributes: provider?.attributes.map((attribute) => ({ ...attribute })) ?? [{ name: "email", source: "email" }, { name: "displayName", source: "name" }],
    allowIdpInitiated: provider?.allowIdpInitiated ?? false,
    defaultRelayState: provider?.defaultRelayState ?? "",
    assertionLifetimeMinutes: provider?.assertionLifetimeMinutes ?? 5,
    isActive: provider?.isActive ?? true
  };
}

/** Fills the form from the service provider's metadata, keeping what the metadata does not say. */
export function applyMetadata(values: SamlAppFormValues, metadata: SamlServiceProviderMetadata): SamlAppFormValues {
  const format = metadata.nameIdFormat && (nameIdFormats as readonly string[]).includes(metadata.nameIdFormat) ? metadata.nameIdFormat as typeof nameIdFormats[number] : values.nameIdFormat;
  return {
    ...values,
    entityId: metadata.entityId,
    assertionConsumerServiceUrls: metadata.assertionConsumerServiceUrls.length ? metadata.assertionConsumerServiceUrls.join("\n") : values.assertionConsumerServiceUrls,
    singleLogoutServiceUrl: metadata.singleLogoutServiceUrl ?? values.singleLogoutServiceUrl,
    nameIdFormat: format,
    signingCertificate: metadata.signingCertificate ?? values.signingCertificate,
    requireSignedRequests: metadata.requireSignedRequests || values.requireSignedRequests,
    encryptionCertificate: metadata.encryptionCertificate ?? values.encryptionCertificate
  };
}

function payload(values: SamlAppFormValues) {
  const parsed = samlAppSchema.parse(values);
  return {
    name: parsed.name,
    entityId: parsed.entityId,
    assertionConsumerServiceUrls: lines(parsed.assertionConsumerServiceUrls),
    singleLogoutServiceUrl: parsed.singleLogoutServiceUrl || null,
    nameIdFormat: parsed.nameIdFormat,
    signingCertificate: parsed.signingCertificate || null,
    requireSignedRequests: parsed.requireSignedRequests,
    encryptionCertificate: parsed.encryptionCertificate || null,
    encryptAssertions: parsed.encryptAssertions,
    signResponse: parsed.signResponse,
    attributes: parsed.attributes.map((attribute) => ({ name: attribute.name, source: attribute.source })),
    allowIdpInitiated: parsed.allowIdpInitiated,
    defaultRelayState: parsed.defaultRelayState || null,
    assertionLifetimeMinutes: parsed.assertionLifetimeMinutes
  };
}

export function createSamlAppPayload(values: SamlAppFormValues) {
  return { applicationSystemId: values.applicationSystemId, ...payload(values) };
}

export function updateSamlAppPayload(values: SamlAppFormValues, version: number) {
  return { ...payload(values), isActive: values.isActive, version };
}

export function describeSource(source: string): string {
  if (source.startsWith("profile:")) return `Perfil: ${source.slice("profile:".length)}`;
  return attributeSources.find((item) => item.value === source)?.label ?? source;
}
