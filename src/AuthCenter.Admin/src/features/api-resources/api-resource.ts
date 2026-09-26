import { z } from "zod";
import type { ApiResource } from "../../api/types";

/** OpenID Connect scopes are built in; an API cannot redefine them (mirrors the server). */
const RESERVED_SCOPES = new Set(["openid", "profile", "email", "offline_access"]);

const scopeSchema = z.object({
  name: z.string().trim()
    .regex(/^[a-z][a-z0-9_.:-]{1,127}$/, "Usa de 2 a 128 caracteres en minúsculas: letras, números, '.', '_', ':' o '-', empezando con letra.")
    .refine((value) => !RESERVED_SCOPES.has(value), "Es un scope de OpenID Connect."),
  displayName: z.string().trim().min(1, "El nombre visible es obligatorio.").max(200, "Usa máximo 200 caracteres."),
  description: z.string().trim().max(1000, "Usa máximo 1000 caracteres.")
});

export const apiResourceSchema = z.object({
  applicationSystemId: z.string(),
  identifier: z.string().trim(),
  displayName: z.string().trim().min(1, "El nombre es obligatorio.").max(200, "Usa máximo 200 caracteres."),
  description: z.string().trim().max(1000, "Usa máximo 1000 caracteres."),
  isActive: z.boolean(),
  scopes: z.array(scopeSchema).min(1, "Define al menos un scope.").max(100, "Define como máximo 100 scopes.")
}).superRefine((values, context) => {
  const seen = new Set<string>();
  values.scopes.forEach((scope, index) => {
    const name = scope.name.trim();
    if (seen.has(name)) context.addIssue({ code: "custom", path: ["scopes", index, "name"], message: "El nombre del scope se repite." });
    seen.add(name);
  });
});

export const newApiResourceSchema = apiResourceSchema.superRefine((values, context) => {
  if (!/^[0-9a-f-]{36}$/i.test(values.applicationSystemId)) context.addIssue({ code: "custom", path: ["applicationSystemId"], message: "Selecciona la aplicación dueña del API." });
  if (!isResourceIndicator(values.identifier)) context.addIssue({ code: "custom", path: ["identifier"], message: "Usa una URI absoluta https: o urn: sin fragmento, por ejemplo https://api.example.com/orders." });
});

export type ApiResourceFormValues = z.input<typeof apiResourceSchema>;

export function isResourceIndicator(value: string): boolean {
  const text = value.trim();
  if (!text || text.length > 300 || /\s/.test(text)) return false;
  try {
    const url = new URL(text);
    return (url.protocol === "https:" || url.protocol === "urn:") && !url.hash && !url.username && !url.password;
  } catch {
    return false;
  }
}

export function apiResourceDefaults(resource?: ApiResource): ApiResourceFormValues {
  return resource
    ? {
      applicationSystemId: resource.applicationSystemId,
      identifier: resource.identifier,
      displayName: resource.displayName,
      description: resource.description ?? "",
      isActive: resource.isActive,
      scopes: resource.scopes.map((scope) => ({ name: scope.name, displayName: scope.displayName, description: scope.description ?? "" }))
    }
    : { applicationSystemId: "", identifier: "", displayName: "", description: "", isActive: true, scopes: [{ name: "", displayName: "", description: "" }] };
}

function scopesPayload(scopes: ApiResourceFormValues["scopes"]) {
  return scopes.map((scope) => ({ name: scope.name.trim(), displayName: scope.displayName.trim(), description: scope.description.trim() || null }));
}

export function createApiResourcePayload(values: ApiResourceFormValues) {
  const parsed = newApiResourceSchema.parse(values);
  return { applicationSystemId: parsed.applicationSystemId, identifier: parsed.identifier, displayName: parsed.displayName, description: parsed.description || null, scopes: scopesPayload(parsed.scopes) };
}

/** The update replaces the complete scope list: a scope left out is removed. */
export function updateApiResourcePayload(values: ApiResourceFormValues) {
  const parsed = apiResourceSchema.parse(values);
  return { displayName: parsed.displayName, description: parsed.description || null, isActive: parsed.isActive, scopes: scopesPayload(parsed.scopes) };
}

/** Scopes present before and missing now: clients requesting them will be refused. */
export function removedScopes(resource: ApiResource | undefined, values: ApiResourceFormValues): string[] {
  if (!resource) return [];
  const kept = new Set(values.scopes.map((scope) => scope.name.trim()));
  return resource.scopes.map((scope) => scope.name).filter((name) => !kept.has(name));
}
