import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type ReactNode } from "react";
import { useForm, useWatch } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { apiRequest, ApiError } from "../../api/client";
import type { DirectoryGroupSummary, DynamicGroupRule, GroupRulePreview, PagedResult, ProfileAttributeDefinition } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { formatDate } from "../../utils/format";
import { convertExpectedValue, describeRule, groupRuleCreatePayload, groupRuleDefaults, groupRuleFromResponse, groupRuleSchema, groupRuleUpdatePayload, type GroupRuleFormValues } from "./group-rule";

const typeHints: Record<ProfileAttributeDefinition["dataType"], string> = {
  String: "Texto exacto, distingue mayúsculas.",
  Integer: "Número entero, por ejemplo 3.",
  Decimal: "Número decimal, por ejemplo 4.5.",
  Boolean: "true o false.",
  Date: "Fecha ISO 8601, por ejemplo 2026-08-13.",
  DateTime: "Fecha y hora ISO 8601 en UTC."
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
    queryFn: ({ signal }) => apiRequest<PagedResult<DirectoryGroupSummary>>("/api/groups?page=1&pageSize=100&isActive=true", { signal })
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
  const definition = schema.data?.find((item) => item.id === selectedDefinitionId);

  const save = useMutation({
    mutationFn: (values: GroupRuleFormValues) => {
      const converted = convertExpectedValue(values.expectedValue, definition?.dataType);
      if (!converted.ok) { setValueError(converted.error); return Promise.reject(new Error(converted.error)); }
      setValueError("");
      return create
        ? apiRequest<DynamicGroupRule>("/api/lifecycle/group-rules", { method: "POST", body: JSON.stringify(groupRuleCreatePayload(values, converted.value)) })
        : apiRequest<DynamicGroupRule>(`/api/lifecycle/group-rules/${ruleId}`, { method: "PUT", body: JSON.stringify(groupRuleUpdatePayload(values, converted.value, current?.version ?? 0)) });
    },
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ["group-rules"] });
      if (create) { navigate(`/group-rules/${result.id}`, { replace: true }); return; }
      queryClient.setQueryData(["group-rule", ruleId], result);
      await queryClient.invalidateQueries({ queryKey: ["group-rule-preview", ruleId] });
      setFeedback(`La regla quedó guardada como versión ${result.version}. La vista previa se actualizó.`);
    }
  });
  const remove = useMutation({
    mutationFn: () => apiRequest<void>(`/api/lifecycle/group-rules/${ruleId}`, { method: "DELETE" }),
    onSuccess: async () => {
      setConfirmDelete(false);
      await queryClient.invalidateQueries({ queryKey: ["group-rules"] });
      navigate("/group-rules", { replace: true });
    }
  });

  if (!create && rule.isPending) return <PageState title="Cargando group rule" busy />;
  if (!create && rule.isError) return <PageState title="No pudimos cargar la group rule" detail={message(rule.error)} tone="error" action={<Link className="button" to="/group-rules">Volver</Link>} />;
  if (!canReadSchema) return <PageState title="No puedes administrar group rules" detail="Necesitas AUTHCENTER_PROFILE_SCHEMAS_READ para elegir el atributo evaluado por la regla." tone="forbidden" action={<Link className="button" to="/group-rules">Volver</Link>} />;
  const title = create ? "Nueva group rule" : current ? `${current.groupName}: ${describeRule(current)}` : "Group rule";
  const conflict = save.error instanceof ApiError && save.error.code === "CONCURRENCY_CONFLICT";
  const formError = conflict || !(save.error instanceof ApiError) ? null : save.error;
  const users = preview.data?.users;

  return <>
    <Breadcrumbs items={[{ label: "Group rules", to: "/group-rules" }, { label: title }]} />
    <PageHeader eyebrow={create ? "Alta" : current?.groupName ?? "Lifecycle"} title={title} description={create ? "Cuando el atributo del perfil coincide con el valor esperado, el usuario se incorpora al grupo de forma automática." : canWrite ? "Ajusta la condición y revisa qué usuarios quedarían dentro antes de activar la regla." : "Consulta la condición y la vista previa de usuarios afectados. No tienes permisos de escritura."} actions={<Link className="button button--secondary" to="/group-rules">Volver al listado</Link>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    {conflict ? <p className="alert alert--error" role="alert">La regla cambió desde que la cargaste. Recarga para ver la versión vigente antes de volver a guardar. <button className="button button--small button--secondary" type="button" onClick={() => { save.reset(); void rule.refetch(); }}>Recargar</button></p> : null}
    {formError ? <p className="alert alert--error" role="alert">{message(formError)}</p> : null}
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => save.mutateAsync(values).catch(() => undefined))(event)}>
      <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="group-rule-definition">
          <div className="settings-panel__heading"><div><h2 id="group-rule-definition">Condición</h2><p>Un grupo admite una regla por atributo. El valor se compara de forma exacta con el tipo del atributo.</p></div>{current ? <StatusBadge active={current.isActive} activeLabel="Activa" inactiveLabel="Inactiva" /> : null}</div>
          <div className="form-grid">
            {create ? <Field label="Grupo" error={form.formState.errors.directoryGroupId?.message}><select {...form.register("directoryGroupId")}><option value="">Selecciona un grupo</option>{groups.data?.items.map((group) => <option key={group.id} value={group.id}>{group.name}</option>)}</select></Field>
              : <Field label="Grupo" error={undefined} help="El grupo de una regla no se puede cambiar; crea una nueva regla si es necesario."><input value={current?.groupName ?? ""} readOnly /></Field>}
            <Field label="Atributo del perfil" error={form.formState.errors.profileAttributeDefinitionId?.message}><select {...form.register("profileAttributeDefinitionId")}><option value="">Selecciona un atributo</option>{schema.data?.filter((item) => item.isActive).map((item) => <option key={item.id} value={item.id}>{item.displayName} ({item.key}, {item.dataType})</option>)}</select></Field>
            <Field label="Operador" error={form.formState.errors.operator?.message}><select {...form.register("operator")}><option value="eq">Es igual a</option></select></Field>
            <Field label="Valor esperado" error={form.formState.errors.expectedValue?.message ?? valueError} help={definition ? typeHints[definition.dataType] : "Selecciona un atributo para conocer el tipo esperado."}>
              {definition && definition.allowedValues.length > 0
                ? <select {...form.register("expectedValue")}><option value="">Selecciona un valor</option>{definition.allowedValues.map((value) => <option key={String(value)} value={String(value)}>{String(value)}</option>)}</select>
                : definition?.dataType === "Boolean"
                  ? <select {...form.register("expectedValue")}><option value="">Selecciona un valor</option><option value="true">true</option><option value="false">false</option></select>
                  : <input {...form.register("expectedValue")} className="mono" autoComplete="off" spellCheck={false} inputMode={definition?.dataType === "Integer" || definition?.dataType === "Decimal" ? "decimal" : undefined} />}
            </Field>
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
      {preview.isError ? <PageState title="No pudimos calcular la vista previa" detail={message(preview.error)} tone="error" action={<button className="button" type="button" onClick={() => void preview.refetch()}>Reintentar</button>} /> : null}
      {users && users.items.length === 0 ? <PageState title="Ningún usuario cumple la condición" detail="Revisa el valor esperado o confirma que los perfiles ya tienen el atributo poblado." /> : null}
      {users?.items.length ? <>
        <div className="data-table" tabIndex={0} role="region" aria-label="Usuarios que cumplen la regla, desplazamiento horizontal"><table><caption className="sr-only">Usuarios que cumplen la regla</caption><thead><tr><th>Usuario</th><th>Correo</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{users.items.map((user) => <tr key={user.id}><td><strong>{user.fullName}</strong></td><td>{user.email}</td><td className="table-action"><Link className="button button--small button--secondary" to={`/users/${user.id}`}>Ver usuario</Link></td></tr>)}</tbody></table></div>
        <Pagination page={users.page} pageSize={users.pageSize} totalCount={users.totalCount} totalPages={users.totalPages} onPageChange={setPreviewPage} onPageSizeChange={(value) => { setPreviewPageSize(value); setPreviewPage(1); }} />
      </> : null}
    </section> : null}
    {current && canWrite ? <section className="settings-panel settings-panel--actions" aria-labelledby="group-rule-danger">
      <div className="settings-panel__heading"><div><h2 id="group-rule-danger">Eliminar regla</h2><p>Las membresías ya otorgadas se conservan; solo dejan de evaluarse nuevas coincidencias.</p></div></div>
      {remove.error ? <p className="alert alert--error" role="alert">{message(remove.error)}</p> : null}
      <div className="button-group"><button className="button button--danger-quiet" type="button" onClick={() => { remove.reset(); setConfirmDelete(true); }}>Eliminar regla</button></div>
    </section> : null}
    <ConfirmDialog open={confirmDelete} title="Eliminar group rule" detail={`Se eliminará la regla ${current ? describeRule(current) : ""} del grupo ${current?.groupName ?? ""}. Esta acción queda auditada y no se puede deshacer.`} confirmLabel="Eliminar" dangerous busy={remove.isPending} onCancel={() => setConfirmDelete(false)} onConfirm={() => remove.mutate()} />
  </>;
}

function Field({ label, error, help, children }: { label: string; error: string | undefined; help?: string; children: ReactNode }) { return <label className="field"><span>{label}</span>{children}{help ? <span className="field-help">{help}</span> : null}{error ? <span className="field-error">{error}</span> : null}</label>; }
function message(error: unknown): string { return error instanceof ApiError ? error.message : "Ocurrió un error inesperado."; }
