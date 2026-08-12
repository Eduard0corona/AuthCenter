import { z } from "zod";
import type { UserSummary } from "../../api/types";

export const userIdentitySchema = z.object({
  fullName: z.string().trim().min(2, "El nombre es obligatorio.").max(200, "Usa máximo 200 caracteres."),
  pictureUrl: z.string().trim().max(2048, "La URL es demasiado larga.").refine((value) => !value || /^https:\/\//i.test(value), "Usa una URL HTTPS.")
});
export type UserIdentityForm = z.infer<typeof userIdentitySchema>;
export function userIdentityDefaults(user?: UserSummary): UserIdentityForm { return { fullName: user?.fullName ?? "", pictureUrl: user?.pictureUrl ?? "" }; }
export function userIdentityPayload(values: UserIdentityForm) { return { fullName: values.fullName.trim(), pictureUrl: values.pictureUrl.trim() || null }; }
