import { z } from "zod";
import type { ProfileMapping } from "../../api/types";

const sourcePathSchema = z.string().trim().min(1, "La ruta de origen es obligatoria.").max(300, "Usa máximo 300 caracteres.")
  .regex(/^\$?\.?[A-Za-z0-9_:-]+(\.[A-Za-z0-9_:-]+)*$/, "Usa una ruta de propiedades separada por puntos, por ejemplo name.givenName.");

export const profileMappingSchema = z.object({
  applicationSystemId: z.string().uuid("Selecciona una aplicación válida."),
  sourcePath: sourcePathSchema,
  targetAttributeDefinitionId: z.string().uuid("Selecciona un atributo destino válido."),
  isAuthoritative: z.boolean(),
  isActive: z.boolean()
});

export type ProfileMappingFormValues = z.input<typeof profileMappingSchema>;

export function profileMappingDefaults(): ProfileMappingFormValues {
  return { applicationSystemId: "", sourcePath: "", targetAttributeDefinitionId: "", isAuthoritative: false, isActive: true };
}

export function profileMappingFromResponse(mapping: ProfileMapping): ProfileMappingFormValues {
  return {
    applicationSystemId: mapping.applicationSystemId,
    sourcePath: mapping.sourcePath,
    targetAttributeDefinitionId: mapping.targetAttributeDefinitionId,
    isAuthoritative: mapping.isAuthoritative,
    isActive: mapping.isActive
  };
}

/** Body for POST /api/lifecycle/profile-mappings and /validate. The backend only accepts SCIM sources. */
export function profileMappingCreatePayload(values: ProfileMappingFormValues) {
  const parsed = profileMappingSchema.parse(values);
  return { applicationSystemId: parsed.applicationSystemId, sourceSystem: "SCIM", sourcePath: parsed.sourcePath, targetAttributeDefinitionId: parsed.targetAttributeDefinitionId, isAuthoritative: parsed.isAuthoritative };
}

/** Body for PUT /api/lifecycle/profile-mappings/{id}; carries the loaded version for optimistic concurrency. */
export function profileMappingUpdatePayload(values: ProfileMappingFormValues, version: number) {
  const parsed = profileMappingSchema.parse(values);
  return { sourceSystem: "SCIM", sourcePath: parsed.sourcePath, targetAttributeDefinitionId: parsed.targetAttributeDefinitionId, isAuthoritative: parsed.isAuthoritative, isActive: parsed.isActive, version };
}

export const sampleSourceDocument = JSON.stringify({
  userName: "grace@example.test",
  name: { givenName: "Grace", familyName: "Hopper" },
  "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": { department: "Ingeniería" }
}, null, 2);

export type SourceDocumentParse = { ok: true; document: Record<string, unknown> } | { ok: false; error: string };

/** Parses the operator-provided SCIM document; the simulation endpoint requires a JSON object. */
export function parseSourceDocument(text: string): SourceDocumentParse {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return { ok: false, error: "El documento debe ser JSON válido." };
  }
  if (value === null || typeof value !== "object" || Array.isArray(value)) return { ok: false, error: "El documento debe ser un objeto JSON." };
  return { ok: true, document: value as Record<string, unknown> };
}

export function formatSimulatedValue(value: unknown): string {
  if (value === null || value === undefined) return "—";
  if (typeof value === "string") return value;
  return JSON.stringify(value, null, 2);
}
