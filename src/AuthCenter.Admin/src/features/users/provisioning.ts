import { z } from "zod";

export type UserProvisioningMode = "create" | "invite";

export const userProvisioningSchema = (mode: UserProvisioningMode) => z.object({
  fullName: z.string().trim().min(2, "El nombre es obligatorio.").max(200, "Usa máximo 200 caracteres."),
  email: z.email("Escribe un correo válido.").max(256, "Usa máximo 256 caracteres."),
  password: z.string(),
  applicationSystemId: z.string(),
  grantApplicationAccess: z.boolean(),
  applicationAccessIsActive: z.boolean(),
  roleIds: z.array(z.string())
}).superRefine((values, context) => {
  if (mode === "create" && !isStrongTemporaryPassword(values.password)) {
    context.addIssue({ code: "custom", path: ["password"], message: "Usa al menos 12 caracteres, mayúscula, minúscula y número." });
  }
  if ((mode === "invite" || values.grantApplicationAccess) && !values.applicationSystemId) {
    context.addIssue({ code: "custom", path: ["applicationSystemId"], message: "Selecciona una aplicación." });
  }
});

export type UserProvisioningForm = z.infer<ReturnType<typeof userProvisioningSchema>>;

export function userProvisioningDefaults(mode: UserProvisioningMode): UserProvisioningForm {
  return {
    fullName: "",
    email: "",
    password: "",
    applicationSystemId: "",
    grantApplicationAccess: mode === "invite",
    applicationAccessIsActive: true,
    roleIds: []
  };
}

export function userProvisioningPayload(mode: UserProvisioningMode, values: UserProvisioningForm) {
  const hasApplication = mode === "invite" || values.grantApplicationAccess;
  const common = {
    fullName: values.fullName.trim(),
    email: values.email.trim().toLowerCase(),
    applicationSystemId: hasApplication ? values.applicationSystemId : null,
    roleIds: hasApplication ? values.roleIds : []
  };
  if (mode === "invite") {
    return { ...common, applicationSystemId: values.applicationSystemId, grantActiveAccess: values.applicationAccessIsActive };
  }
  return {
    ...common,
    password: values.password,
    isTemporaryPassword: true,
    grantApplicationAccess: values.grantApplicationAccess,
    applicationAccessIsActive: values.applicationAccessIsActive
  };
}

export function generateTemporaryPassword(randomValues?: Uint8Array): string {
  const upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
  const lower = "abcdefghijkmnopqrstuvwxyz";
  const digits = "23456789";
  const symbols = "!@#$%_-";
  const pool = `${upper}${lower}${digits}${symbols}`;
  const bytes = randomValues ?? crypto.getRandomValues(new Uint8Array(40));
  if (bytes.length < 40) throw new Error("At least 40 random bytes are required.");
  const characters = [upper.charAt(bytes[0]! % upper.length), lower.charAt(bytes[1]! % lower.length), digits.charAt(bytes[2]! % digits.length), symbols.charAt(bytes[3]! % symbols.length)];
  for (let index = 4; index < 20; index += 1) characters.push(pool.charAt(bytes[index]! % pool.length));
  for (let index = characters.length - 1; index > 0; index -= 1) {
    const target = bytes[20 + index]! % (index + 1);
    [characters[index], characters[target]] = [characters[target]!, characters[index]!];
  }
  return characters.join("");
}

function isStrongTemporaryPassword(value: string): boolean {
  return value.length >= 12 && /[A-Z]/.test(value) && /[a-z]/.test(value) && /[0-9]/.test(value);
}
