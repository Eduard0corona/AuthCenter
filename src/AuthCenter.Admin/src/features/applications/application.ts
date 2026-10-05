import { z } from "zod";
import type { ApplicationAudience, ApplicationRegistrationSettings, ApplicationSummary } from "../../api/types";

const domainPattern = /^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/i;

export const audiences = ["Employees", "Consumers"] as const satisfies readonly ApplicationAudience[];

/** The audience as the form offers it, and as the application list shows it. */
export const audienceOptions: Record<ApplicationAudience, string> = {
  Employees: "Empleados — personas de tu organización",
  Consumers: "Consumidores — clientes de tu producto"
};
export const audienceLabels: Record<ApplicationAudience, string> = { Employees: "Empleados", Consumers: "Consumidores" };

/**
 * The registration that fits an audience on a new application, while the operator has not chosen
 * one: consumers create their own account (confirming their email); employees are let in by an
 * administrator, so registration stays closed.
 */
export function registrationForAudience(audience: ApplicationAudience): { registrationMode: ApplicationFormValues["registrationMode"]; requireEmailConfirmation?: boolean } {
  return audience === "Consumers" ? { registrationMode: "Open", requireEmailConfirmation: true } : { registrationMode: "Closed" };
}

/** The audience of an application; a response from before audiences existed is read as the server reads it. */
export function audienceOf(settings: Pick<ApplicationRegistrationSettings, "registrationMode"> & { audience?: ApplicationAudience | undefined }): ApplicationAudience {
  return settings.audience ?? (settings.registrationMode === "Open" ? "Consumers" : "Employees");
}

export const applicationSchema = z.object({
  code: z.string().trim().min(2, "Usa al menos 2 caracteres.").max(32, "Usa máximo 32 caracteres.")
    .regex(/^[A-Za-z][A-Za-z0-9_]*$/, "Empieza con una letra y usa letras, números o guion bajo."),
  name: z.string().trim().min(2, "El nombre es obligatorio.").max(120, "Usa máximo 120 caracteres."),
  description: z.string().trim().max(500, "Usa máximo 500 caracteres."),
  audience: z.enum(audiences),
  registrationMode: z.enum(["Closed", "Open", "InviteOnly", "ApprovalRequired"]),
  allowPasswordLogin: z.boolean(),
  allowMagicLink: z.boolean(),
  allowGoogleLogin: z.boolean(),
  allowMicrosoftLogin: z.boolean(),
  allowGitHubLogin: z.boolean(),
  allowAppleLogin: z.boolean(),
  requireEmailConfirmation: z.boolean(),
  requireMfa: z.boolean(),
  defaultRoleId: z.string().uuid().nullable(),
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
    audience: audienceOf(settings),
    registrationMode: settings.registrationMode,
    allowPasswordLogin: settings.allowPasswordLogin,
    allowMagicLink: settings.allowMagicLink,
    allowGoogleLogin: settings.allowGoogleLogin,
    allowMicrosoftLogin: settings.allowMicrosoftLogin,
    allowGitHubLogin: settings.allowGitHubLogin,
    allowAppleLogin: settings.allowAppleLogin,
    requireEmailConfirmation: settings.requireEmailConfirmation,
    requireMfa: settings.requireMfa,
    defaultRoleId: settings.defaultRoleId,
    allowedEmailDomains: settings.allowedEmailDomains ?? ""
  };
}

export function applicationPayload(values: ApplicationFormValues, application?: ApplicationSummary) {
  return {
    ...(application ? { version: application.version } : { code: values.code.trim().toUpperCase() }),
    name: values.name.trim(),
    description: values.description.trim() || null,
    audience: values.audience,
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
    defaultRoleId: values.defaultRoleId
  };
}

const defaultRegistrationSettings: ApplicationRegistrationSettings = {
  registrationMode: "Closed",
  audience: "Employees",
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
