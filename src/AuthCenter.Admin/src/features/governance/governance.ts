import { z } from "zod";
import type { AccessRequestSource, AccessRequestStatus, AccessReviewDecision, AccessReviewStatus, SeparationOfDutiesHolding } from "../../api/types";

export const requestStatusLabels: Record<AccessRequestStatus, string> = {
  Pending: "Pendiente",
  Approved: "Aprobada",
  Rejected: "Rechazada",
  Cancelled: "Cancelada",
  Expired: "Expirada"
};

export const requestSourceLabels: Record<AccessRequestSource, string> = {
  Portal: "Portal",
  Registration: "Registro con aprobación",
  Administrator: "Alta de un administrador"
};

export const reviewStatusLabels: Record<AccessReviewStatus, string> = {
  Active: "En curso",
  Completed: "Completada",
  Cancelled: "Cancelada"
};

export const decisionLabels: Record<AccessReviewDecision, string> = {
  Pending: "Sin revisar",
  Keep: "Se mantiene",
  Revoke: "Revocado"
};

/** Spanish text for the governance error codes the pages can meet. */
export const governanceMessages: Record<string, string> = {
  ACCESS_REQUEST_NOT_PENDING: "La solicitud ya no está pendiente: alguien la decidió o expiró.",
  ACCESS_REQUEST_EXPIRED: "La solicitud expiró; la persona puede pedir el acceso de nuevo.",
  ACCESS_REQUEST_INVALID: "Escribe el motivo del rechazo: la persona lo leerá.",
  SELF_APPROVAL_FORBIDDEN: "Nadie decide su propia solicitud de acceso.",
  SELF_REVIEW_FORBIDDEN: "Nadie revisa su propio acceso.",
  USER_INACTIVE: "La cuenta de quien pidió el acceso está inactiva o eliminada.",
  ROLE_INVALID: "El rol pedido ya no se puede otorgar.",
  APP_INACTIVE: "La aplicación está inactiva.",
  ACCESS_REVIEW_ACTIVE_EXISTS: "La aplicación ya tiene una revisión en curso; complétala o cancélala antes.",
  ACCESS_REVIEW_ITEM_DECIDED: "Ese acceso ya fue revisado.",
  ACCESS_REVIEW_NOT_ACTIVE: "La revisión ya terminó.",
  SOD_RULE_EXISTS: "Ya existe una regla con ese nombre o para esos dos roles.",
  GOVERNANCE_INVALID: "Los responsables deben ser usuarios activos (hasta 20)."
};

/** How a user holds a role in a violation: directly, through groups, or both. */
export function describeHolding(holding: SeparationOfDutiesHolding): string {
  const role = holding.applicationCode ? `${holding.applicationCode} · ${holding.roleName}` : holding.roleName;
  const sources = [holding.direct ? "directo" : null, holding.groups.length ? `grupos: ${holding.groups.join(", ")}` : null].filter(Boolean);
  return `${role} (${sources.join("; ") || "sin origen"})`;
}

export const reviewSchema = z.object({
  name: z.string().trim().min(1, "Escribe un nombre.").max(150, "Máximo 150 caracteres."),
  applicationSystemId: z.string().min(1, "Elige la aplicación."),
  dueAt: z.string().min(1, "Elige la fecha límite."),
  revokeUnreviewed: z.boolean(),
  recurrenceMonths: z.string()
}).superRefine((values, context) => {
  const due = new Date(values.dueAt);
  if (Number.isNaN(due.getTime())) { context.addIssue({ code: "custom", path: ["dueAt"], message: "La fecha no es válida." }); return; }
  const now = Date.now();
  if (due.getTime() < now + 60 * 60 * 1000 || due.getTime() > now + 366 * 24 * 60 * 60 * 1000)
    context.addIssue({ code: "custom", path: ["dueAt"], message: "La fecha límite va de una hora a un año desde ahora." });
});

export type ReviewFormValues = z.infer<typeof reviewSchema>;

/** Two weeks from now, as a datetime-local value. */
export function reviewDefaults(now = new Date()): ReviewFormValues {
  const due = new Date(now.getTime() + 14 * 24 * 60 * 60 * 1000);
  due.setSeconds(0, 0);
  return { name: "", applicationSystemId: "", dueAt: toLocalInput(due), revokeUnreviewed: false, recurrenceMonths: "" };
}

export function reviewPayload(values: ReviewFormValues) {
  return {
    name: values.name.trim(),
    applicationSystemId: values.applicationSystemId,
    dueAt: new Date(values.dueAt).toISOString(),
    revokeUnreviewed: values.revokeUnreviewed,
    recurrenceMonths: values.recurrenceMonths ? Number(values.recurrenceMonths) : null
  };
}

export const sodRuleSchema = z.object({
  name: z.string().trim().min(1, "Escribe un nombre.").max(150, "Máximo 150 caracteres."),
  description: z.string().max(1000, "Máximo 1000 caracteres."),
  firstRoleId: z.string().min(1, "Elige el primer rol."),
  secondRoleId: z.string().min(1, "Elige el segundo rol."),
  isActive: z.boolean()
}).refine((values) => values.firstRoleId !== values.secondRoleId, { path: ["secondRoleId"], message: "Elige dos roles distintos." });

export type SodRuleFormValues = z.infer<typeof sodRuleSchema>;

export function sodRulePayload(values: SodRuleFormValues, version?: number) {
  return {
    name: values.name.trim(),
    description: values.description.trim() || null,
    firstRoleId: values.firstRoleId,
    secondRoleId: values.secondRoleId,
    isActive: values.isActive,
    ...(version === undefined ? {} : { version })
  };
}

/** A Date as the value of a datetime-local input, in the browser's time zone. */
export function toLocalInput(date: Date): string {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}
