import { z } from "zod";
import type { PermissionSummary } from "../../api/types";

export const permissionSchema = z.object({
  applicationSystemId: z.string().uuid("Selecciona una aplicación válida."),
  code: z.string().trim().min(3, "El código es obligatorio.").max(100, "Usa máximo 100 caracteres.").regex(/^[A-Z0-9_]+$/, "Usa mayúsculas, números y guion bajo."),
  name: z.string().trim().min(2, "El nombre es obligatorio.").max(200, "Usa máximo 200 caracteres."),
  description: z.string().trim().max(500, "Usa máximo 500 caracteres.")
});

export type PermissionFormValues = z.infer<typeof permissionSchema>;

export function permissionDefaults(permission?: PermissionSummary): PermissionFormValues {
  return { applicationSystemId: permission?.applicationSystemId ?? "", code: permission?.code ?? "", name: permission?.name ?? "", description: permission?.description ?? "" };
}

export function permissionPayload(values: PermissionFormValues, create: boolean) {
  return {
    ...(create ? { applicationSystemId: values.applicationSystemId, code: values.code.trim().toUpperCase() } : {}),
    name: values.name.trim(),
    description: values.description.trim() || null
  };
}
