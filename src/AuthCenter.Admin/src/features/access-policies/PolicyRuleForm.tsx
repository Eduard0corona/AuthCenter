import { zodResolver } from "@hookform/resolvers/zod";
import { useEffect, useState, type ReactNode } from "react";
import { Controller, useForm, useWatch, type UseFormRegisterReturn } from "react-hook-form";
import type { AccessPolicyRule, DirectoryGroupSummary } from "../../api/types";
import { UserPicker } from "../../components/UserPicker";
import { assuranceLabels, assuranceLevels, dayLabels, policyDays, policyRuleDefaults, policyRuleSchema, riskLabels, riskLevels, type PolicyRuleFormValues } from "./policy";

interface PolicyRuleFormProps {
  rule: AccessPolicyRule | undefined;
  busy: boolean;
  error: string;
  /** The application of the policy: the user search only offers users with access to it. */
  applicationSystemId: string;
  /** With AUTHCENTER_USERS_READ the target user is searched by name; otherwise its UUID is typed. */
  canSearchUsers: boolean;
  groups: DirectoryGroupSummary[];
  onSave: (values: PolicyRuleFormValues) => Promise<void>;
  onCancel: () => void;
}

export function PolicyRuleForm({ rule, busy, error, applicationSystemId, canSearchUsers, groups, onSave, onCancel }: PolicyRuleFormProps) {
  const [userLabel, setUserLabel] = useState(rule?.userEmail ?? "");
  const form = useForm<PolicyRuleFormValues>({ resolver: zodResolver(policyRuleSchema), defaultValues: policyRuleDefaults(rule) });
  const targetType = useWatch({ control: form.control, name: "targetType" });
  useEffect(() => form.reset(policyRuleDefaults(rule)), [form, rule]);

  return <section className="settings-panel" aria-labelledby="policy-rule-editor">
    <div className="settings-panel__heading"><div><h2 id="policy-rule-editor">{rule ? "Editar regla" : "Nueva regla"}</h2><p>La menor prioridad se evalúa primero. Si ninguna regla activa coincide, se deniega el acceso.</p></div></div>
    {error ? <p className="alert alert--error" role="alert">{error}</p> : null}
    <form className="form-stack" onSubmit={(event) => void form.handleSubmit(onSave)(event)}>
      <div className="form-grid"><Field label="Nombre" error={form.formState.errors.name?.message}><input {...form.register("name")} autoComplete="off" /></Field><Field label="Prioridad" error={form.formState.errors.priority?.message}><input type="number" min={1} {...form.register("priority", { valueAsNumber: true })} /></Field></div>
      <div className="form-grid"><Field label="Objetivo" error={form.formState.errors.targetType?.message}><select {...form.register("targetType")}><option value="all">Todos</option><option value="user">Usuario</option><option value="group">Grupo</option></select></Field>{targetType === "user" && canSearchUsers ? <Controller control={form.control} name="targetId" render={({ field, fieldState }) => <UserPicker label="Usuario objetivo" value={field.value} selectedLabel={userLabel} onChange={(userId, label) => { field.onChange(userId); setUserLabel(label); }} applicationSystemId={applicationSystemId} error={fieldState.error?.message} />} /> : targetType !== "all" ? <Field label={targetType === "user" ? "Usuario objetivo" : "Grupo objetivo"} error={form.formState.errors.targetId?.message}>{targetType === "group" && groups.length ? <select {...form.register("targetId")}><option value="">Selecciona un grupo</option>{groups.map((group) => <option key={group.id} value={group.id}>{group.name}</option>)}</select> : <input {...form.register("targetId")} placeholder="UUID" autoComplete="off" />}</Field> : <input type="hidden" {...form.register("targetId")} />}</div>
      <div className="form-grid"><Field label="Acción" error={form.formState.errors.action?.message}><select {...form.register("action")}><option value="Allow">Permitir</option><option value="Deny">Denegar</option></select></Field><Field label="MFA" error={form.formState.errors.mfaRequirement?.message}><select {...form.register("mfaRequirement")}><option value="Optional">Opcional</option><option value="Required">Obligatorio</option></select></Field></div>
      <div className="form-grid"><Field label="CIDR incluidos" error={form.formState.errors.includedIpCidrs?.message} help="Uno por línea; vacío acepta cualquier red."><textarea rows={3} {...form.register("includedIpCidrs")} /></Field><Field label="CIDR excluidos" error={form.formState.errors.excludedIpCidrs?.message} help="Las exclusiones tienen precedencia."><textarea rows={3} {...form.register("excludedIpCidrs")} /></Field></div>
      <fieldset className="check-group"><legend>Días UTC</legend><div className="checkbox-grid">{policyDays.map((day) => <Checkbox key={day} label={dayLabels[day]} registration={form.register("activeDaysUtc")} value={day} />)}</div></fieldset>
      <div className="form-grid"><Field label="Activa desde UTC" error={form.formState.errors.activeFromUtc?.message}><input type="datetime-local" {...form.register("activeFromUtc")} /></Field><Field label="Activa hasta UTC" error={form.formState.errors.activeUntilUtc?.message}><input type="datetime-local" {...form.register("activeUntilUtc")} /></Field><Field label="Inicio diario UTC" error={form.formState.errors.dailyStartTimeUtc?.message}><input type="time" {...form.register("dailyStartTimeUtc")} /></Field><Field label="Fin diario UTC" error={form.formState.errors.dailyEndTimeUtc?.message}><input type="time" {...form.register("dailyEndTimeUtc")} /></Field></div>
      <div className="form-grid"><Field label="Riesgo mínimo" error={form.formState.errors.minimumRiskLevel?.message}><RiskSelect registration={form.register("minimumRiskLevel")} /></Field><Field label="Riesgo máximo" error={form.formState.errors.maximumRiskLevel?.message}><RiskSelect registration={form.register("maximumRiskLevel")} /></Field><Field label="Nivel de autenticación requerido" error={form.formState.errors.requiredAssuranceLevel?.message}><select {...form.register("requiredAssuranceLevel")}>{assuranceLevels.map((level) => <option key={level} value={level}>{assuranceLabels[level]}</option>)}</select></Field></div>
      <div className="checkbox-grid"><Checkbox label="Regla activa" registration={form.register("isActive")} /><Checkbox label="Omitir el MFA en dispositivos de confianza" registration={form.register("allowTrustedDeviceBypass")} /></div>
      <div className="form-footer"><button className="button button--secondary" type="button" onClick={onCancel} disabled={busy}>Cancelar</button><button className="button" disabled={busy}>{busy ? "Guardando…" : rule ? "Guardar regla" : "Crear regla"}</button></div>
    </form>
  </section>;
}

function Field({ label, error, help, children }: { label: string; error: string | undefined; help?: string | undefined; children: ReactNode }) { return <label className="field"><span>{label}</span>{children}{help ? <span className="field-help">{help}</span> : null}{error ? <span className="field-error">{error}</span> : null}</label>; }
function Checkbox({ label, registration, value }: { label: string; registration: UseFormRegisterReturn; value?: string }) { return <label className="checkbox-field"><input type="checkbox" {...registration} value={value} /><span>{label}</span></label>; }
function RiskSelect({ registration }: { registration: UseFormRegisterReturn }) { return <select {...registration}>{riskLevels.map((risk) => <option key={risk || "none"} value={risk}>{risk ? riskLabels[risk] : "Sin condición"}</option>)}</select>; }
