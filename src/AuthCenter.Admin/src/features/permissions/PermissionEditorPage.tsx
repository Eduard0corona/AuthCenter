import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, PermissionSummary } from "../../api/types";
import { needPermission } from "../../auth/permissions";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { HistoryLink } from "../../components/HistoryLink";
import { SaveError } from "../../components/SaveError";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { StatusBadge } from "../../components/StatusBadge";
import { permissionDefaults, permissionPayload, permissionSchema, type PermissionFormValues } from "./permission";

export default function PermissionEditorPage({ create = false }: { create?: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_PERMISSIONS_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const { permissionId } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [confirmStatus, setConfirmStatus] = useState(false);
  const [feedback, setFeedback] = useState("");
  const permission = useQuery({ queryKey: ["permission", permissionId], enabled: !create && Boolean(permissionId), queryFn: ({ signal }) => apiRequest<PermissionSummary>(`/api/permissions/${permissionId}`, { signal }) });
  const applications = useQuery({ queryKey: ["applications", "permission-editor"], enabled: canReadApplications, queryFn: ({ signal }) => fetchAllAsPage<ApplicationSummary>("/api/applications", signal) });
  const form = useForm<PermissionFormValues>({ resolver: zodResolver(permissionSchema), defaultValues: permissionDefaults() });
  useEffect(() => { if (permission.data) form.reset(permissionDefaults(permission.data)); }, [form, permission.data]);
  const save = useMutation({
    mutationFn: (values: PermissionFormValues) => apiRequest<PermissionSummary>(create ? "/api/permissions" : `/api/permissions/${permissionId}`, { method: create ? "POST" : "PUT", body: JSON.stringify(create ? permissionPayload(values, create) : { ...permissionPayload(values, create), version: permission.data?.version }) }),
    onSuccess: async (saved) => { queryClient.setQueryData(["permission", saved.id], saved); await queryClient.invalidateQueries({ queryKey: ["permissions"] }); if (create) navigate(`/permissions/${saved.id}`, { replace: true }); else { form.reset(permissionDefaults(saved)); setFeedback("El permiso quedó actualizado."); } }
  });
  const changeStatus = useMutation({ mutationFn: () => apiRequest<void>(`/api/permissions/${permissionId}/${permission.data?.isActive ? "deactivate" : "activate"}`, { method: "PATCH" }), onSuccess: async () => { setConfirmStatus(false); setFeedback("El estado del permiso quedó actualizado."); await Promise.all([queryClient.invalidateQueries({ queryKey: ["permission", permissionId] }), queryClient.invalidateQueries({ queryKey: ["permissions"] })]); } });
  if (!create && permission.isPending) return <PageState title="Cargando permiso" busy />;
  if (!create && permission.isError) return <PageState title="No pudimos cargar el permiso" detail={errorMessage(permission.error)} tone="error" />;
  const current = permission.data;
  const title = create ? "Nuevo permiso" : current?.name ?? "Permiso";
  if (create && !canReadApplications) return <PageState tone="forbidden" title="Acceso complementario requerido" detail={needPermission("AUTHCENTER_APPLICATIONS_READ", "elegir la aplicación del nuevo permiso")} />;
  return <>
    <Breadcrumbs items={[{ label: "Permisos", to: "/permissions" }, { label: title }]} />
    <PageHeader eyebrow={current?.code ?? "Catálogo"} title={title} description={canWrite ? "Define una capacidad estable y explícita para una aplicación." : "Consulta la capacidad efectiva. Tu acceso es de sólo lectura."} actions={<>{create ? null : <HistoryLink entityName="Permission" entityId={permissionId} />}<Link className="button button--secondary" to="/permissions">Volver</Link></>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}<SaveError error={save.error} onReload={() => { save.reset(); void permission.refetch().then((fresh) => { if (fresh.data) form.reset(permissionDefaults(fresh.data)); }); }} />
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => save.mutateAsync(values))(event)}><fieldset className="settings-fieldset" disabled={!canWrite}><section className="settings-panel"><div className="settings-panel__heading"><div><h2>Identidad del permiso</h2><p>El código es inmutable después del alta para no romper integraciones.</p></div>{current ? <StatusBadge active={current.isActive} /> : null}</div><div className="form-grid"><Field label="Aplicación" error={form.formState.errors.applicationSystemId?.message}><select {...form.register("applicationSystemId")} disabled={!create}><option value="">Selecciona…</option>{applications.data?.items.map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></Field><Field label="Código" error={form.formState.errors.code?.message}><input {...form.register("code")} disabled={!create} placeholder="APP_RESOURCE_ACTION" /></Field></div><Field label="Nombre" error={form.formState.errors.name?.message}><input {...form.register("name")} /></Field><Field label="Descripción" error={form.formState.errors.description?.message}><textarea {...form.register("description")} rows={3} /></Field></section></fieldset>{canWrite ? <div className="form-footer"><Link className="button button--secondary" to="/permissions">Cancelar</Link><button className="button" disabled={save.isPending}>{save.isPending ? "Guardando…" : create ? "Crear permiso" : "Guardar"}</button></div> : null}</form>
    {current && canWrite ? <section className="settings-panel settings-panel--actions"><div className="settings-panel__heading"><div><h2>Operación</h2><p>Desactivar un permiso lo excluye del acceso efectivo aunque siga asignado.</p></div></div><button className={`button ${current.isActive ? "button--danger-quiet" : "button--secondary"}`} onClick={() => setConfirmStatus(true)}>{current.isActive ? "Desactivar" : "Activar"}</button></section> : null}
    <ConfirmDialog open={confirmStatus} title={`${current?.isActive ? "Desactivar" : "Activar"} permiso`} detail="La API calculará nuevamente el acceso efectivo de los usuarios." confirmLabel={current?.isActive ? "Desactivar" : "Activar"} dangerous={Boolean(current?.isActive)} busy={changeStatus.isPending} error={changeStatus.error} onCancel={() => setConfirmStatus(false)} onConfirm={() => changeStatus.mutate()} />
  </>;
}

function Field({ label, error, children }: { label: string; error: string | undefined; children: React.ReactNode }) { return <label className="field"><span>{label}</span>{children}{error ? <span className="field-error">{error}</span> : null}</label>; }
