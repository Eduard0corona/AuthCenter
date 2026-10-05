import { z } from "zod";
import type { AccessPolicyRule, AccessPolicyVersion } from "../../api/types";

export const policyDays = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"] as const;
export const riskLevels = ["", "Unknown", "Low", "Medium", "High", "Critical"] as const;
/** The risk of a simulated sign-in ("" is only a rule's "no condition"). */
export const simulationRiskLevels = ["Unknown", "Low", "Medium", "High", "Critical"] as const;
export const assuranceLevels = ["Password", "Mfa", "PhishingResistant"] as const;

// The API's values, as the console shows them.
export const dayLabels: Record<(typeof policyDays)[number], string> = {
  Monday: "Lunes", Tuesday: "Martes", Wednesday: "Miércoles", Thursday: "Jueves", Friday: "Viernes", Saturday: "Sábado", Sunday: "Domingo"
};
export const riskLabels: Record<(typeof simulationRiskLevels)[number], string> = {
  Unknown: "Desconocido", Low: "Bajo", Medium: "Medio", High: "Alto", Critical: "Crítico"
};
export const assuranceLabels: Record<(typeof assuranceLevels)[number], string> = {
  Password: "Contraseña", Mfa: "Verificación en dos pasos (MFA)", PhishingResistant: "Resistente a phishing (llave de acceso)"
};
export const actionLabels: Record<AccessPolicyRule["action"], string> = { Allow: "Permitir", Deny: "Denegar" };
export const versionStatusLabels: Record<AccessPolicyVersion["status"], string> = { Draft: "Borrador", Published: "Publicada", Archived: "Archivada" };

const uuidOrEmpty = z.string().refine((value) => !value || z.string().uuid().safeParse(value).success, "Usa un UUID válido.");

export const policyRuleSchema = z.object({
  name: z.string().trim().min(2, "El nombre es obligatorio.").max(200, "Usa máximo 200 caracteres."),
  priority: z.coerce.number().int().min(1, "Usa una prioridad mayor a cero."),
  targetType: z.enum(["all", "user", "group"]),
  targetId: uuidOrEmpty,
  action: z.enum(["Allow", "Deny"]),
  mfaRequirement: z.enum(["Optional", "Required"]),
  allowTrustedDeviceBypass: z.boolean(),
  includedIpCidrs: z.string(),
  excludedIpCidrs: z.string(),
  activeFromUtc: z.string(),
  activeUntilUtc: z.string(),
  activeDaysUtc: z.array(z.enum(policyDays)),
  dailyStartTimeUtc: z.string(),
  dailyEndTimeUtc: z.string(),
  minimumRiskLevel: z.enum(riskLevels),
  maximumRiskLevel: z.enum(riskLevels),
  requiredAssuranceLevel: z.enum(assuranceLevels),
  isActive: z.boolean()
}).superRefine((values, context) => {
  if (values.targetType !== "all" && !values.targetId) context.addIssue({ code: "custom", path: ["targetId"], message: "Especifica el UUID del objetivo." });
  if (Boolean(values.dailyStartTimeUtc) !== Boolean(values.dailyEndTimeUtc)) context.addIssue({ code: "custom", path: ["dailyEndTimeUtc"], message: "Configura inicio y fin diario juntos." });
  if (values.dailyStartTimeUtc && values.dailyStartTimeUtc === values.dailyEndTimeUtc) context.addIssue({ code: "custom", path: ["dailyEndTimeUtc"], message: "El inicio y fin no pueden ser iguales." });
  if (values.activeFromUtc && values.activeUntilUtc && values.activeFromUtc >= values.activeUntilUtc) context.addIssue({ code: "custom", path: ["activeUntilUtc"], message: "El fin debe ser posterior al inicio." });
  const min = riskLevels.indexOf(values.minimumRiskLevel);
  const max = riskLevels.indexOf(values.maximumRiskLevel);
  if (min > 0 && max > 0 && min > max) context.addIssue({ code: "custom", path: ["maximumRiskLevel"], message: "El riesgo máximo no puede ser menor al mínimo." });
});

export type PolicyRuleFormValues = z.input<typeof policyRuleSchema>;

export function policyRuleDefaults(rule?: AccessPolicyRule): PolicyRuleFormValues {
  return {
    name: rule?.name ?? "",
    priority: rule?.priority ?? 100,
    targetType: rule?.userId ? "user" : rule?.directoryGroupId ? "group" : "all",
    targetId: rule?.userId ?? rule?.directoryGroupId ?? "",
    action: rule?.action ?? "Allow",
    mfaRequirement: rule?.mfaRequirement ?? "Optional",
    allowTrustedDeviceBypass: rule?.allowTrustedDeviceBypass ?? true,
    includedIpCidrs: rule?.includedIpCidrs.join("\n") ?? "",
    excludedIpCidrs: rule?.excludedIpCidrs.join("\n") ?? "",
    activeFromUtc: toInputDate(rule?.activeFromUtc),
    activeUntilUtc: toInputDate(rule?.activeUntilUtc),
    activeDaysUtc: rule?.activeDaysUtc as PolicyRuleFormValues["activeDaysUtc"] ?? [],
    dailyStartTimeUtc: rule?.dailyStartTimeUtc?.slice(0, 5) ?? "",
    dailyEndTimeUtc: rule?.dailyEndTimeUtc?.slice(0, 5) ?? "",
    minimumRiskLevel: (rule?.minimumRiskLevel ?? "") as PolicyRuleFormValues["minimumRiskLevel"],
    maximumRiskLevel: (rule?.maximumRiskLevel ?? "") as PolicyRuleFormValues["maximumRiskLevel"],
    requiredAssuranceLevel: rule?.requiredAssuranceLevel ?? "Password",
    isActive: rule?.isActive ?? true
  };
}

export function policyRulePayload(values: PolicyRuleFormValues, applicationSystemId: string, policyVersionId: string, create: boolean) {
  const parsed = policyRuleSchema.parse(values);
  return {
    ...(create ? { applicationSystemId, policyVersionId } : {}),
    userId: parsed.targetType === "user" ? parsed.targetId : null,
    directoryGroupId: parsed.targetType === "group" ? parsed.targetId : null,
    name: parsed.name,
    priority: parsed.priority,
    action: parsed.action,
    mfaRequirement: parsed.mfaRequirement,
    allowTrustedDeviceBypass: parsed.allowTrustedDeviceBypass,
    includedIpCidrs: lines(parsed.includedIpCidrs),
    excludedIpCidrs: lines(parsed.excludedIpCidrs),
    activeFromUtc: toUtc(parsed.activeFromUtc),
    activeUntilUtc: toUtc(parsed.activeUntilUtc),
    activeDaysUtc: parsed.activeDaysUtc,
    dailyStartTimeUtc: parsed.dailyStartTimeUtc || null,
    dailyEndTimeUtc: parsed.dailyEndTimeUtc || null,
    minimumRiskLevel: parsed.minimumRiskLevel || null,
    maximumRiskLevel: parsed.maximumRiskLevel || null,
    requiredAssuranceLevel: parsed.requiredAssuranceLevel,
    isActive: parsed.isActive
  };
}

export interface PolicyDiffEntry { priority: number; name: string; kind: "added" | "removed" | "changed"; }

export function diffPolicyRules(draft: AccessPolicyRule[], published: AccessPolicyRule[]): PolicyDiffEntry[] {
  const draftByPriority = new Map(draft.map((rule) => [rule.priority, rule]));
  const publishedByPriority = new Map(published.map((rule) => [rule.priority, rule]));
  const priorities = [...new Set([...draftByPriority.keys(), ...publishedByPriority.keys()])].sort((a, b) => a - b);
  const result: PolicyDiffEntry[] = [];
  for (const priority of priorities) {
    const next = draftByPriority.get(priority);
    const previous = publishedByPriority.get(priority);
    if (!previous && next) result.push({ priority, name: next.name, kind: "added" });
    else if (previous && !next) result.push({ priority, name: previous.name, kind: "removed" });
    else if (previous && next && comparable(previous) !== comparable(next)) result.push({ priority, name: next.name, kind: "changed" });
  }
  return result;
}

function comparable(rule: AccessPolicyRule): string {
  return JSON.stringify([
    rule.userId, rule.directoryGroupId, rule.name, rule.priority,
    rule.action, rule.mfaRequirement, rule.allowTrustedDeviceBypass,
    rule.includedIpCidrs, rule.excludedIpCidrs, rule.activeFromUtc,
    rule.activeUntilUtc, rule.activeDaysUtc, rule.dailyStartTimeUtc,
    rule.dailyEndTimeUtc, rule.minimumRiskLevel, rule.maximumRiskLevel,
    rule.requiredAssuranceLevel, rule.isActive
  ]);
}
function lines(value: string): string[] { return value.split(/\r?\n|,/).map((item) => item.trim()).filter(Boolean); }
function toUtc(value: string): string | null { return value ? new Date(`${value}:00Z`).toISOString() : null; }
function toInputDate(value?: string | null): string { return value ? new Date(value).toISOString().slice(0, 16) : ""; }
