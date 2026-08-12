import { z } from "zod";
import type { ApplicationRegistrationSettings, ApplicationSummary } from "../../api/types";

const domainPattern = /^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/i;

export const applicationSchema = z.object({
  code: z.string().trim().min(2, "Usa al menos 2 caracteres.").max(32, "Usa máximo 32 caracteres.")
    .regex(/^[A-Za-z][A-Za-z0-9_-]*$/, "Empieza con una letra y usa letras, números, guion o guion bajo."),
  name: z.string().trim().min(2, "El nombre es obligatorio.").max(120, "Usa máximo 120 caracteres."),
  description: z.string().trim().max(500, "Usa máximo 500 caracteres."),
  registrationMode: z.enum(["Closed", "Open", "InviteOnly", "ApprovalRequired"]),
  allowPasswordLogin: z.boolean(),
  allowMagicLink: z.boolean(),
  allowGoogleLogin: z.boolean(),
  allowMicrosoftLogin: z.boolean(),
  allowGitHubLogin: z.boolean(),
  allowAppleLogin: z.boolean(),
  requireEmailConfirmation: z.boolean(),
  requireMfa: z.boolean(),
  allowedEmailDomains: z.string().trim().max(1000, "La lista de dominios es demasiado larga.").refine(
    (value) => !value || value.split(",").map((domain) => domain.trim().replace(/^@/, "")).every((domain) => domainPattern.test(domain)),
    "Usa dominios separados por coma, por ejemplo: empresa.com, filial.mx."
  )
}).refine(
  (value) => value.allowPasswordLogin || value.allowMagicLink || value.allowGoogleLogin || value.allowMicrosoftLogin || value.allowGitHubLogin || value.allowAppleLogin,
  { path: ["allowPasswordLogin"], message: "Habilita al menos un método de autenticación." }
);

export type ApplicationFormValues = z.infer<typeof applicationSchema>;

export function applicationDefaults(application?: ApplicationSummary): ApplicationFormValues {
  const settings = application?.registrationSettings ?? defaultRegistrationSettings;
  return {
    code: application?.code ?? "",
    name: application?.name ?? "",
    description: application?.description ?? "",
    registrationMode: settings.registrationMode,
    allowPasswordLogin: settings.allowPasswordLogin,
    allowMagicLink: settings.allowMagicLink,
    allowGoogleLogin: settings.allowGoogleLogin,
    allowMicrosoftLogin: settings.allowMicrosoftLogin,
    allowGitHubLogin: settings.allowGitHubLogin,
    allowAppleLogin: settings.allowAppleLogin,
    requireEmailConfirmation: settings.requireEmailConfirmation,
    requireMfa: settings.requireMfa,
    allowedEmailDomains: settings.allowedEmailDomains ?? ""
  };
}

export function applicationPayload(values: ApplicationFormValues, application?: ApplicationSummary) {
  return {
    ...(application ? {} : { code: values.code.trim().toUpperCase() }),
    name: values.name.trim(),
    description: values.description.trim() || null,
    registrationMode: values.registrationMode,
    allowPasswordLogin: values.allowPasswordLogin,
    allowMagicLink: values.allowMagicLink,
    allowGoogleLogin: values.allowGoogleLogin,
    allowMicrosoftLogin: values.allowMicrosoftLogin,
    allowGitHubLogin: values.allowGitHubLogin,
    allowAppleLogin: values.allowAppleLogin,
    requireEmailConfirmation: values.requireEmailConfirmation,
    requireMfa: values.requireMfa,
    allowedEmailDomains: values.allowedEmailDomains.trim() || null,
    defaultRoleId: application?.registrationSettings?.defaultRoleId ?? null
  };
}

const defaultRegistrationSettings: ApplicationRegistrationSettings = {
  registrationMode: "Closed",
  allowGoogleLogin: false,
  allowMicrosoftLogin: false,
  allowGitHubLogin: false,
  allowAppleLogin: false,
  allowMagicLink: false,
  allowPasswordLogin: true,
  requireEmailConfirmation: true,
  requireMfa: false,
  allowedEmailDomains: null,
  defaultRoleId: null
};
