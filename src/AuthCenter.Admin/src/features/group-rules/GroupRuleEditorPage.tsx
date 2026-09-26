import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type ReactNode } from "react";
import { useForm, useWatch } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest, ApiError } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { DirectoryGroupSummary, DynamicGroupRule, GroupRulePreview, ProfileAttributeDefinition } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { HistoryLink } from "../../components/HistoryLink";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { formatDate } from "../../utils/format";
import { convertExpectedValue, describeRule, groupRuleCreatePayload, groupRuleDefaults, groupRuleFromResponse, groupRuleSchema, groupRuleUpdatePayload, operatorLabels, operatorsFor, type GroupRuleFormValues } from "./group-rule";

const typeHints: Record<ProfileAttributeDefinition["dataType"], string> = {
  String: "Texto. «Es igual a» distingue mayúsculas; «Contiene» y «Empieza por» no.",
  Integer: "Número entero, por ejemplo 3.",
  Decimal: "Número decimal, por ejemplo 4.5.",
  Boolean: "true o false.",
  Date: "Fecha AAAA-MM-DD, por ejemplo 2026-08-13.",
  DateTime: "Fecha y hora ISO 8601 con zona, por ejemplo 2026-08-13T09:00:00Z."
};

export default function GroupRuleEditorPage({ create = false }: { create?: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_GROUPS_WRITE");
  const canReadSchema = permissions.has("AUTHCENTER_PROFILE_SCHEMAS_READ");
  const { ruleId = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState("");
  const [valueError, setValueError] = useState("");
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [previewPage, setPreviewPage] = useState(1);
  const [previewPageSize, setPreviewPageSize] = useState(20);
  const rule = useQuery({
    queryKey: ["group-rule", ruleId],
    enabled: !create && Boolean(ruleId),
    queryFn: ({ signal }) => apiRequest<DynamicGroupRule>(`/api/lifecycle/group-rules/${ruleId}`, { signal })
  });
  const groups = useQuery({
    queryKey: ["groups", "group-rule-editor"],
    enabled: create,
    queryFn: ({ signal }) => fetchAllAsPage<DirectoryGroupSummary>("/api/groups?isActive=true", signal)
  });
  const schema = useQuery({
    queryKey: ["profile-schema", "active"],
    enabled: canReadSchema,
    queryFn: ({ signal }) => apiRequest<ProfileAttributeDefinition[]>("/api/profile-schema", { signal })
  });
  const preview = useQuery({
    queryKey: ["group-rule-preview", ruleId, previewPage, previewPageSize],
    enabled: !create && Boolean(ruleId),
    queryFn: ({ signal }) => apiRequest<GroupRulePreview>(`/api/lifecycle/group-rules/${ruleId}/preview`, { method: "POST", body: JSON.stringify({ page: previewPage, pageSize: previewPageSize }), signal })
  });
  const form = useForm<GroupRuleFormValues>({ resolver: zodResolver(groupRuleSchema), defaultValues: groupRuleDefaults() });
  const current = rule.data;
  const { reset } = form;
  useEffect(() => { if (current) reset(groupRuleFromResponse(current)); }, [current, reset]);
  const selectedDefinitionId = useWatch({ control: form.control, name: "profileAttributeDefinitionId" });
  const selectedOperator = useWatch({ control: form.control, name: "operator" });
  const definition = schema.data?.find((item) => item.id === selectedDefinitionId);
  const available = operatorsFor(definition?.dataType);
  // The stored operator stays selectable while the schema loads, so the form never shows another one.
  const operatorOptions = available.includes(selectedOperator) ? available : [selectedOperator, ...available];

  const save = useMutation({
    mutationFn: (values: GroupRuleFormValues) => {
      const converted = convertExpectedValue(values.expectedValue, definition?.dataType, values.operator);
      if (!converted.ok) { setValueError(converted.error); return Promise.reject(new Error(converted.error)); }
      setValueError("");
      return create
        ? apiRequest<DynamicGroupRule>("/api/lifecycle/group-rules", { method: "POST", body: JSON.stringify(groupRuleCreatePayload(values, converted.value)) })
        : apiRequest<DynamicGroupRule>(`/api/lifecycle/group-rules/${ruleId}`, { method: "PUT", body: JSON.stringify(groupRuleUpdatePayload(values, converted.value, current?.version ?? 0)) });
    },
    onSuccess: async (result) => {
      await invalidateMembership();
      if (create) { navigate(`/group-rules/${result.id}`, { replace: true }); return; }
      queryClient.setQueryData(["group-rule", ruleId], result);
      await queryClient.invalidateQueries({ queryKey: ["group-rule-preview", ruleId] });
      setFeedback(`La regla quedó guardada como versión ${result.version} y la membresía del grupo se recalculó.`);
    }
  });
  const remove = useMutation({
    mutationFn: () => apiRequest<void>(`/api/lifecycle/group-rules/${ruleId}`, { method: "DELETE" }),
    onSuccess: async () => {
      setConfirmDelete(false);
      await invalidateMembership();
      navigate("/group-rules", { replace: true });
    }
  });

  /** Saving or deleting a rule recomputes the group's members on the server. */
  async function invalidateMembership(): Promise<void> {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["group-rules"] }),
      queryClient.invalidateQueries({ queryKey: ["groups"] }),
      queryClient.invalidateQueries({ queryKey: ["group"] }),
      queryClient.invalidateQueries({ queryKey: ["group-members"] })
    ]);
  }

  function changeDefinition(definitionId: string): void {
    // An operator the new attribute's type does not support falls back to equality.
    const next = schema.data?.find((item) => item.id === definitionId);
    if (!operatorsFor(next?.dataType).includes(form.getValues("operator"))) form.setValue("operator", "eq");
  }

  if (!create && rule.isPending) return <PageState title="Cargando group rule" busy />;
  if (!create && rule.isError) return <PageState title="No pudimos cargar la group rule" detail={errorMessage(rule.error)} tone="error" action={<Link className="button" to="/group-rules">Volver</Link>} />;
  if (!canReadSchema) return <PageState title="No puedes administrar group rules" detail="Necesitas AUTHCENTER_PROFILE_SCHEMAS_READ para elegir el atributo evaluado por la regla." tone="forbidden" action={<Link className="button" to="/group-rules">Volver</Link>} />;
  const title = create ? "Nueva group rule" : current ? `${current.groupName}: ${describeRule(current)}` : "Group rule";
  const conflict = save.error instanceof ApiError && save.error.code === "CONCURRENCY_CONFLICT";
  const formError = conflict || !(save.error instanceof ApiError) ? null : save.error;
  const users = preview.data?.users;

  return <>
    <Breadcrumbs items={[{ label: "Group rules", to: "/group-rules" }, { label: title }]} />
    <PageHeader eyebrow={create ? "Alta" : current?.groupName ?? "Lifecycle"} title={title} description={create ? "Los usuarios cuyo perfil cumple la condición entran al grupo automáticamente y salen cuando deja de cumplirse." : canWrite ? "Ajusta la condición y revisa qué usuarios quedarían dentro antes de activar la regla." : "Consulta la condición y la vista previa de usuarios afectados. No tienes permisos de escritura."} actions={<>{create ? null : <HistoryLink entityName="DynamicGroupRule" entityId={ruleId} />}<Link className="button button--secondary" to="/group-rules">Volver al listado</Link></>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    {conflict ? <p className="alert alert--error" role="alert">La regla cambió desde que la cargaste. Recarga para ver la versión vigente antes de volver a guardar. <button className="button button--small button--secondary" type="button" onClick={() => { save.reset(); void rule.refetch(); }}>Recargar</button></p> : null}
    {formError ? <p className="alert alert--error" role="alert">{errorMessage(formError)}</p> : null}
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => save.mutateAsync(values).catch(() => undefined))(event)}>
      <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="group-rule-definition">
          <div className="settings-panel__heading"><div><h2 id="group-rule-definition">Condición</h2><p>Un grupo admite una regla por atributo y basta con que se cumpla una. Se evalúa el valor guardado en el perfil (el valor por defecto del esquema no cuenta). Al guardar, la membresía del grupo se recalcula para todo el directorio: quien deja de cumplir sus reglas pierde el acceso heredado y sus sesiones.</p></div>{current ? <StatusBadge active={current.isActive} activeLabel="Activa" inactiveLabel="Inactiva" /> : null}</div>
          <div className="form-grid">
            {create ? <Field label="Grupo" error={form.formState.errors.directoryGroupId?.message}><select {...form.register("directoryGroupId")}><option value="">Selecciona un grupo</option>{groups.data?.items.map((group) => <option key={group.id} value={group.id}>{group.name}</option>)}</select></Field>
              : <Field label="Grupo" error={undefined} help="El grupo de una regla no se puede cambiar; crea una nueva regla si es necesario."><input value={current?.groupName ?? ""} readOnly /></Field>}
            <Field label="Atributo del perfil" error={form.formState.errors.profileAttributeDefinitionId?.message}><select {...form.register("profileAttributeDefinitionId", { onChange: (event: { target: { value: string } }) => changeDefinition(event.target.value) })}><option value="">Selecciona un atributo</option>{schema.data?.filter((item) => item.isActive).map((item) => <option key={item.id} value={item.id}>{item.displayName} ({item.key}, {item.dataType})</option>)}</select></Field>
            <Field label="Operador" error={form.formState.errors.operator?.message} help={definition ? undefined : "Elige el atributo para ver todos los operadores de su tipo."}><select {...form.register("operator")}>{operatorOptions.map((operator) => <option key={operator} value={operator}>{operatorLabels[operator]}</option>)}</select></Field>
            {selectedOperator === "exists"
              ? <div className="field"><span>Valor esperado</span><p className="field-help">No hace falta: basta con que el perfil tenga el atributo con cualquier valor.</p></div>
              : <Field label={selectedOperator === "in" ? "Valores esperados" : "Valor esperado"} error={form.formState.errors.expectedValue?.message ?? valueError} help={expectedValueHelp(definition, selectedOperator === "in")}>
                {selectedOperator === "in"
                  ? <textarea {...form.register("expectedValue")} className="mono" rows={4} autoComplete="off" spellCheck={false} />
                  : definition && definition.allowedValues.length > 0 && (selectedOperator === "eq" || selectedOperator === "ne")
                    ? <select {...form.register("expectedValue")}><option value="">Selecciona un valor</option>{definition.allowedValues.map((value) => <option key={String(value)} value={String(value)}>{String(value)}</option>)}</select>
                    : definition?.dataType === "Boolean"
                      ? <select {...form.register("expectedValue")}><option value="">Selecciona un valor</option><option value="true">true</option><option value="false">false</option></select>
                      : <input {...form.register("expectedValue")} className="mono" autoComplete="off" spellCheck={false} inputMode={definition?.dataType === "Integer" || definition?.dataType === "Decimal" ? "decimal" : undefined} />}
              </Field>}
          </div>
          {create ? null : <div className="checkbox-grid"><label className="checkbox-field"><input type="checkbox" {...form.register("isActive")} /><span>Regla activa</span></label></div>}
          {current ? <dl className="profile-summary"><div><dt>Creada</dt><dd>{formatDate(current.createdAt)}</dd></div><div><dt>Versión</dt><dd>{current.version}</dd></div><div><dt>Atributo</dt><dd className="mono">{current.attributeName}</dd></div><div><dt>Identificador</dt><dd className="mono">{current.id}</dd></div></dl> : null}
        </section>
      </fieldset>
      <div className="form-footer">
        <Link className="button button--secondary" to="/group-rules">Cancelar</Link>
        {canWrite ? <button className="button" type="submit" disabled={save.isPending}>{save.isPending ? "Guardando…" : create ? "Crear regla" : "Guardar cambios"}</button> : null}
      </div>
    </form>
    {current ? <section className="settings-panel settings-panel--actions" aria-labelledby="group-rule-preview">
      <div className="settings-panel__heading"><div><h2 id="group-rule-preview">Vista previa de miembros</h2><p>Usuarios activos cuyo perfil cumple la condición guardada. Los cambios sin guardar no se reflejan aquí.</p></div>{users ? <span className="tag">{users.totalCount} usuario{users.totalCount === 1 ? "" : "s"}</span> : null}</div>
      {preview.isPending ? <PageState title="Calculando vista previa" busy /> : null}
      {preview.isError ? <PageState title="No pudimos calcular la vista previa" detail={errorMessage(preview.error)} tone="error" action={<button className="button" type="button" onClick={() => void preview.refetch()}>Reintentar</button>} /> : null}
      {users && users.items.length === 0 ? <PageState title="Ningún usuario cumple la condición" detail="Revisa el valor esperado o confirma que los perfiles ya tienen el atributo guardado." /> : null}
      {users?.items.length ? <>
        <div className="data-table" tabIndex={0} role="region" aria-label="Usuarios que cumplen la regla, desplazamiento horizontal"><table><caption className="sr-only">Usuarios que cumplen la regla</caption><thead><tr><th>Usuario</th><th>Correo</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{users.items.map((user) => <tr key={user.id}><td><strong>{user.fullName}</strong></td><td>{user.email}</td><td className="table-action"><Link className="button button--small button--secondary" to={`/users/${user.id}`}>Ver usuario</Link></td></tr>)}</tbody></table></div>
        <Pagination page={users.page} pageSize={users.pageSize} totalCount={users.totalCount} totalPages={users.totalPages} onPageChange={setPreviewPage} onPageSizeChange={(value) => { setPreviewPageSize(value); setPreviewPage(1); }} />
      </> : null}
    </section> : null}
    {current && canWrite ? <section className="settings-panel settings-panel--actions" aria-labelledby="group-rule-danger">
      <div className="settings-panel__heading"><div><h2 id="group-rule-danger">Eliminar regla</h2><p>Si el grupo conserva otras reglas activas, su membresía se recalcula con ellas; si era la última, los miembros actuales se conservan y el grupo vuelve a administrarse a mano.</p></div></div>
      {remove.error ? <p className="alert alert--error" role="alert">{errorMessage(remove.error)}</p> : null}
      <div className="button-group"><button className="button button--danger-quiet" type="button" onClick={() => { remove.reset(); setConfirmDelete(true); }}>Eliminar regla</button></div>
    </section> : null}
    <ConfirmDialog open={confirmDelete} title="Eliminar group rule" detail={`Se eliminará la regla ${current ? describeRule(current) : ""} del grupo ${current?.groupName ?? ""}. Esta acción queda auditada y no se puede deshacer.`} confirmLabel="Eliminar" dangerous busy={remove.isPending} error={remove.error} onCancel={() => setConfirmDelete(false)} onConfirm={() => remove.mutate()} />
  </>;
}

function expectedValueHelp(definition: ProfileAttributeDefinition | undefined, list: boolean): string {
  if (!definition) return "Selecciona un atributo para conocer el tipo esperado.";
  const allowed = definition.allowedValues.length > 0 ? ` Valores permitidos: ${definition.allowedValues.map(String).join(", ")}.` : "";
  return `${list ? "Un valor por línea, hasta 100. " : ""}${typeHints[definition.dataType]}${allowed}`;
}

function Field({ label, error, help, children }: { label: string; error: string | undefined; help?: string | undefined; children: ReactNode }) { return <label className="field"><span>{label}</span>{children}{help ? <span className="field-help">{help}</span> : null}{error ? <span className="field-error">{error}</span> : null}</label>; }
