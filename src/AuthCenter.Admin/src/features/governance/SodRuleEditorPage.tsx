import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { useApplicationsCatalog, useRolesCatalog } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { SeparationOfDutiesRule } from "../../api/types";
import { needPermission } from "../../auth/permissions";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { Field } from "../../components/Field";
import { HistoryLink } from "../../components/HistoryLink";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { SaveError } from "../../components/SaveError";
import { StatusBadge } from "../../components/StatusBadge";
import { formatDate } from "../../utils/format";
import { governanceMessages, sodRulePayload, sodRuleSchema, type SodRuleFormValues } from "./governance";

const emptyRule: SodRuleFormValues = { name: "", description: "", firstRoleId: "", secondRoleId: "", isActive: true };

export default function SodRuleEditorPage({ create = false }: { create?: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_GOVERNANCE_WRITE");
  const canReadRoles = permissions.has("AUTHCENTER_ROLES_READ");
  const { ruleId = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState("");
  const [confirmDelete, setConfirmDelete] = useState(false);
  const rule = useQuery({
    queryKey: ["sod-rule", ruleId],
    enabled: !create && Boolean(ruleId),
    queryFn: ({ signal }) => apiRequest<SeparationOfDutiesRule>(`/api/governance/sod-rules/${ruleId}`, { signal })
  });
  const roles = useRolesCatalog(null, canReadRoles);
  const applications = useApplicationsCatalog(permissions.has("AUTHCENTER_APPLICATIONS_READ"));
  const form = useForm<SodRuleFormValues>({ resolver: zodResolver(sodRuleSchema), defaultValues: emptyRule });
  const errors = form.formState.errors;
  const current = rule.data;
  const { reset } = form;
  useEffect(() => {
    if (current) reset({ name: current.name, description: current.description ?? "", firstRoleId: current.firstRole.id, secondRoleId: current.secondRole.id, isActive: current.isActive });
  }, [current, reset]);
  const codes = new Map(applications.data?.map((application) => [application.id, application.code]));
  const roleOptions = (roles.data ?? [])
    .map((role) => ({ id: role.id, label: `${role.applicationSystemId ? `${codes.get(role.applicationSystemId) ?? "?"} · ` : ""}${role.name}${role.isActive ? "" : " (inactivo)"}` }))
    .sort((left, right) => left.label.localeCompare(right.label, "es"));

  const save = useMutation({
    mutationFn: (values: SodRuleFormValues) => create
      ? apiRequest<SeparationOfDutiesRule>("/api/governance/sod-rules", { method: "POST", body: JSON.stringify(sodRulePayload(values)) })
      : apiRequest<SeparationOfDutiesRule>(`/api/governance/sod-rules/${ruleId}`, { method: "PUT", body: JSON.stringify(sodRulePayload(values, current?.version ?? 0)) }),
    onSuccess: async (result) => {
      await Promise.all([queryClient.invalidateQueries({ queryKey: ["sod-rules"] }), queryClient.invalidateQueries({ queryKey: ["sod-violations"] })]);
      if (create) { navigate(`/sod-rules/${result.id}`, { replace: true }); return; }
      queryClient.setQueryData(["sod-rule", ruleId], result);
      setFeedback(result.violationCount > 0
        ? `Regla guardada. ${result.violationCount} ${result.violationCount === 1 ? "usuario ya tiene" : "usuarios ya tienen"} los dos roles: revísalos en la lista de violaciones.`
        : "Regla guardada. Nadie tiene los dos roles.");
    }
  });
  const remove = useMutation({
    mutationFn: () => apiRequest<void>(`/api/governance/sod-rules/${ruleId}`, { method: "DELETE" }),
    onSuccess: async () => {
      setConfirmDelete(false);
      await Promise.all([queryClient.invalidateQueries({ queryKey: ["sod-rules"] }), queryClient.invalidateQueries({ queryKey: ["sod-violations"] })]);
      navigate("/sod-rules", { replace: true });
    }
  });

  if (!create && rule.isPending) return <PageState title="Cargando regla" busy />;
  if (!create && rule.isError) return <PageState title="No pudimos cargar la regla" detail={errorMessage(rule.error)} tone="error" action={<Link className="button" to="/sod-rules">Volver</Link>} />;
  if (!canReadRoles) return <PageState title="No puedes editar reglas" detail={needPermission("AUTHCENTER_ROLES_READ", "elegir los roles")} tone="forbidden" action={<Link className="button" to="/sod-rules">Volver</Link>} />;
  const title = create ? "Nueva regla" : current?.name ?? "Regla";

  return <>
    <Breadcrumbs items={[{ label: "Segregación de funciones", to: "/sod-rules" }, { label: title }]} />
    <PageHeader eyebrow="Gobierno" title={title} description="Nadie podrá tener a la vez los dos roles, directamente o por sus grupos. Quien ya los tiene aparece en la lista de violaciones." actions={<>{create ? null : <HistoryLink entityName="SeparationOfDutiesRule" entityId={ruleId} />}<Link className="button button--secondary" to="/sod-rules">Volver al listado</Link></>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    <SaveError error={save.error} messages={governanceMessages} onReload={() => { save.reset(); void rule.refetch(); }} />
    <form className="settings-form" noValidate onSubmit={(event) => void form.handleSubmit((values) => save.mutateAsync(values).catch(() => undefined))(event)}>
      <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="sod-rule-definition">
          <div className="settings-panel__heading"><div><h2 id="sod-rule-definition">Regla</h2><p>Los roles pueden ser de aplicaciones distintas.</p></div>{current ? <StatusBadge active={current.isActive} activeLabel="Activa" inactiveLabel="Inactiva" /> : null}</div>
          <div className="form-grid">
            <Field label="Nombre" error={errors.name?.message}><input {...form.register("name")} autoComplete="off" placeholder="Solicitar y aprobar pagos" /></Field>
            <Field label="Primer rol" error={errors.firstRoleId?.message}><select {...form.register("firstRoleId")}><option value="">Selecciona un rol</option>{roleOptions.map((role) => <option key={role.id} value={role.id}>{role.label}</option>)}</select></Field>
            <Field label="Segundo rol" error={errors.secondRoleId?.message}><select {...form.register("secondRoleId")}><option value="">Selecciona un rol</option>{roleOptions.map((role) => <option key={role.id} value={role.id}>{role.label}</option>)}</select></Field>
            <Field label="Descripción" error={errors.description?.message} help="Por qué los roles son incompatibles; se muestra en la lista."><textarea {...form.register("description")} rows={3} /></Field>
          </div>
          <div className="checkbox-grid"><label className="checkbox-field"><input type="checkbox" {...form.register("isActive")} /><span>Regla activa (una regla inactiva no impide ni reporta nada)</span></label></div>
          {current ? <dl className="profile-summary"><div><dt>Violaciones</dt><dd>{current.violationCount}</dd></div><div><dt>Creada</dt><dd>{formatDate(current.createdAt)}</dd></div><div><dt>Versión</dt><dd>{current.version}</dd></div></dl> : null}
        </section>
      </fieldset>
      <div className="form-footer">
        <Link className="button button--secondary" to="/sod-rules">Cancelar</Link>
        {canWrite ? <button className="button" type="submit" disabled={save.isPending}>{save.isPending ? "Guardando…" : create ? "Crear regla" : "Guardar cambios"}</button> : null}
      </div>
    </form>
    {current && canWrite ? <section className="settings-panel settings-panel--actions" aria-labelledby="sod-rule-danger">
      <div className="settings-panel__heading"><div><h2 id="sod-rule-danger">Eliminar regla</h2><p>Los roles dejarán de considerarse incompatibles. Para pausarla sin perderla, desactívala.</p></div></div>
      {remove.error ? <p className="alert alert--error" role="alert">{errorMessage(remove.error)}</p> : null}
      <div className="button-group"><button className="button button--danger-quiet" type="button" onClick={() => { remove.reset(); setConfirmDelete(true); }}>Eliminar regla</button></div>
    </section> : null}
    <ConfirmDialog open={confirmDelete} title="Eliminar la regla" detail={`Se eliminará la regla ${current?.name ?? ""}. Esta acción queda auditada y no se puede deshacer.`} confirmLabel="Eliminar" dangerous busy={remove.isPending} error={remove.error} onCancel={() => setConfirmDelete(false)} onConfirm={() => remove.mutate()} />
  </>;
}
