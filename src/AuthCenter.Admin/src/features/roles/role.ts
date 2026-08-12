import { z } from "zod";
import type { RoleSummary } from "../../api/types";

export const roleSchema = z.object({
  name: z.string().trim().min(2, "El nombre es obligatorio.").max(256, "Usa máximo 256 caracteres."),
  description: z.string().trim().max(500, "Usa máximo 500 caracteres."),
  applicationSystemId: z.string().uuid("Selecciona una aplicación válida.")
});

export type RoleFormValues = z.infer<typeof roleSchema>;

export function roleDefaults(role?: RoleSummary): RoleFormValues {
  return {
    name: role?.name ?? "",
    description: role?.description ?? "",
    applicationSystemId: role?.applicationSystemId ?? ""
  };
}

export function rolePayload(values: RoleFormValues) {
  return {
    name: values.name.trim(),
    description: values.description.trim() || null,
    applicationSystemId: values.applicationSystemId,
    isSystemRole: false
  };
}
