import { zodResolver } from "@hookform/resolvers/zod";
import { useEffect, type ReactNode } from "react";
import { useForm, useWatch } from "react-hook-form";
import type { DirectoryGroupSummary, FederationProvider, FederationRoutingRule, ProfileAttributeDefinition } from "../../api/types";
import { routingRuleDefaults, routingRuleSchema, type RoutingRuleFormValues } from "./federation";

interface RoutingRuleFormProps {
  rule: FederationRoutingRule | undefined;
  nextPriority: number;
  providers: FederationProvider[];
  groups: DirectoryGroupSummary[];
  schema: ProfileAttributeDefinition[];
  error: string;
  onSave: (values: RoutingRuleFormValues) => void;
  onCancel: () => void;
}

export function RoutingRuleForm({ rule, nextPriority, providers, groups, schema, error, onSave, onCancel }: RoutingRuleFormProps) {
  const form = useForm<RoutingRuleFormValues>({ resolver: zodResolver(routingRuleSchema), defaultValues: routingRuleDefaults(rule, nextPriority) });
  const { reset } = form;
  useEffect(() => reset(routingRuleDefaults(rule, nextPriority)), [reset, rule, nextPriority]);
  const attributeId = useWatch({ control: form.control, name: "profileAttributeDefinitionId" });
  const definition = schema.find((item) => item.id === attributeId);

  return <section className="settings-panel" aria-labelledby="routing-rule-editor">
    <div className="settings-panel__heading"><div><h2 id="routing-rule-editor">{rule ? "Editar routing rule" : "Nueva routing rule"}</h2><p>Se evalúa la primera regla activa que coincida, en orden de prioridad. Las condiciones se combinan con Y.</p></div></div>
    {error ? <p className="alert alert--error" role="alert">{error}</p> : null}
    <form className="form-stack" onSubmit={(event) => void form.handleSubmit(onSave)(event)}>
      <div className="form-grid">
        {rule ? <Field label="Proveedor" error={undefined}><input value={rule.providerName} readOnly /></Field>
          : <Field label="Proveedor" error={form.formState.errors.federationProviderId?.message}><select {...form.register("federationProviderId")}><option value="">Selecciona un proveedor</option>{providers.map((provider) => <option key={provider.id} value={provider.id}>{provider.name} ({provider.protocol === "Saml2" ? "SAML" : "OIDC"})</option>)}</select></Field>}
        <Field label="Prioridad" error={form.formState.errors.priority?.message} help="Menor número se evalúa primero; debe ser única por proveedor."><input type="number" min={1} max={10000} {...form.register("priority", { valueAsNumber: true })} /></Field>
      </div>
      <div className="form-grid">
        <Field label="Dominio de correo" error={form.formState.errors.emailDomain?.message} help="Sin @, por ejemplo empresa.com."><input {...form.register("emailDomain")} className="mono" autoComplete="off" spellCheck={false} /></Field>
        <Field label="Grupo del directorio" error={form.formState.errors.directoryGroupId?.message}><select {...form.register("directoryGroupId")}><option value="">Sin condición de grupo</option>{groups.map((group) => <option key={group.id} value={group.id}>{group.name}</option>)}</select></Field>
        <Field label="Atributo del perfil" error={form.formState.errors.profileAttributeDefinitionId?.message}><select {...form.register("profileAttributeDefinitionId")}><option value="">Sin condición de atributo</option>{schema.filter((item) => item.isActive).map((item) => <option key={item.id} value={item.id}>{item.displayName} ({item.key}, {item.dataType})</option>)}</select></Field>
        {attributeId ? <Field label="Valor esperado" error={form.formState.errors.expectedValue?.message} help={definition ? `Se compara como ${definition.dataType}.` : undefined}><input {...form.register("expectedValue")} className="mono" autoComplete="off" spellCheck={false} /></Field> : <input type="hidden" {...form.register("expectedValue")} />}
      </div>
      <div className="checkbox-grid"><label className="checkbox-field"><input type="checkbox" {...form.register("isActive")} /><span>Regla activa</span></label></div>
      <div className="form-footer"><button className="button button--secondary" type="button" onClick={onCancel}>Cancelar</button><button className="button">{rule ? "Verificar y guardar" : "Verificar y crear"}</button></div>
    </form>
  </section>;
}

function Field({ label, error, help, children }: { label: string; error: string | undefined; help?: string | undefined; children: ReactNode }) { return <label className="field"><span>{label}</span>{children}{help ? <span className="field-help">{help}</span> : null}{error ? <span className="field-error">{error}</span> : null}</label>; }
