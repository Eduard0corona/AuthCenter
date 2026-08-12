import { z } from "zod";
import type { DirectoryGroupSummary } from "../../api/types";

export const groupSchema = z.object({
  name: z.string().trim().min(2, "El nombre es obligatorio.").max(200, "Usa máximo 200 caracteres."),
  description: z.string().trim().max(1000, "Usa máximo 1000 caracteres.")
});

export type GroupFormValues = z.infer<typeof groupSchema>;

export function groupDefaults(group?: DirectoryGroupSummary): GroupFormValues {
  return { name: group?.name ?? "", description: group?.description ?? "" };
}

export function groupPayload(values: GroupFormValues) {
  return { name: values.name.trim(), description: values.description.trim() || null };
}
