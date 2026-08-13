import { z } from "zod";

export const provisioningScopes = [
  "scim.users.read",
  "scim.users.write",
  "scim.groups.read",
  "scim.groups.write"
] as const;

const expirationSchema = z.string().min(1, "La expiración es obligatoria.").superRefine((value, context) => {
  const expiresAt = new Date(value);
  const now = new Date();
  if (Number.isNaN(expiresAt.valueOf())) {
    context.addIssue({ code: "custom", message: "Indica una fecha válida." });
    return;
  }
  if (expiresAt <= now) context.addIssue({ code: "custom", message: "La expiración debe estar en el futuro." });
  if (expiresAt > new Date(now.getTime() + 366 * 24 * 60 * 60 * 1000)) {
    context.addIssue({ code: "custom", message: "La vigencia máxima es de un año." });
  }
});

export const provisioningTokenSchema = z.object({
  applicationSystemId: z.string().uuid("Selecciona una aplicación válida."),
  name: z.string().trim().min(1, "El nombre es obligatorio.").max(150, "Usa máximo 150 caracteres."),
  scopes: z.array(z.enum(provisioningScopes)).min(1, "Selecciona al menos un scope."),
  expiresAt: expirationSchema
});

export const provisioningTokenRotationSchema = z.object({ expiresAt: expirationSchema });

export type ProvisioningTokenFormValues = z.input<typeof provisioningTokenSchema>;
export type ProvisioningTokenRotationValues = z.input<typeof provisioningTokenRotationSchema>;

export function defaultExpiration(days = 90): string {
  const value = new Date(Date.now() + days * 24 * 60 * 60 * 1000);
  value.setSeconds(0, 0);
  const local = new Date(value.getTime() - value.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

export function provisioningTokenDefaults(): ProvisioningTokenFormValues {
  return { applicationSystemId: "", name: "", scopes: ["scim.users.read"], expiresAt: defaultExpiration() };
}

export function provisioningTokenPayload(values: ProvisioningTokenFormValues) {
  const parsed = provisioningTokenSchema.parse(values);
  return { ...parsed, expiresAt: new Date(parsed.expiresAt).toISOString() };
}

export function provisioningTokenRotationPayload(values: ProvisioningTokenRotationValues) {
  const parsed = provisioningTokenRotationSchema.parse(values);
  return new Date(parsed.expiresAt).toISOString();
}
