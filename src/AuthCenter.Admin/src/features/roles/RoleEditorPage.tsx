import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, PermissionSummary, RoleSummary } from "../../api/types";
import { needPermission, permissionLabel } from "../../auth/permissions";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { HistoryLink } from "../../components/HistoryLink";
import { SaveError } from "../../components/SaveError";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { StatusBadge } from "../../components/StatusBadge";
import { roleDefaults, rolePayload, roleSchema, type RoleFormValues } from "./role";

export default function RoleEditorPage({ create = false }: { create?: boolean }) {
  const { permissions: sessionPermissions } = useSession();
  const canWrite = sessionPermissions.has("AUTHCENTER_ROLES_WRITE");
  const canReadPermissions = sessionPermissions.has("AUTHCENTER_PERMISSIONS_READ");
  const canReadApplications = sessionPermissions.has("AUTHCENTER_APPLICATIONS_READ");
  const { roleId } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [permissionOverrides, setPermissionOverrides] = useState<Map<string, boolean>>(new Map());
  const [permissionSearch, setPermissionSearch] = useState("");
  const [feedback, setFeedback] = useState("");
  const [confirmStatus, setConfirmStatus] = useState(false);
  const role = useQuery({ queryKey: ["role", roleId], enabled: !create && Boolean(roleId), queryFn: ({ signal }) => apiRequest<RoleSummary>(`/api/roles/${roleId}`, { signal }) });
  const applications = useQuery({ queryKey: ["applications", "role-editor"], enabled: canReadApplications, queryFn: ({ signal }) => fetchAllAsPage<ApplicationSummary>("/api/applications", signal) });
  const form = useForm<RoleFormValues>({ resolver: zodResolver(roleSchema), defaultValues: roleDefaults() });
  const applicationId = useWatch({ control: form.control, name: "applicationSystemId" });
  const availablePermissions = useQuery({ queryKey: ["permissions", "role-editor", applicationId], enabled: !create && canReadPermissions && Boolean(applicationId), queryFn: ({ signal }) => fetchAllAsPage<PermissionSummary>(`/api/applications/${applicationId}/permissions`, signal) });
  useEffect(() => { if (role.data) form.reset(roleDefaults(role.data)); }, [form, role.data]);
  const selectedPermissions = useMemo(() => {
    const selected = new Set(role.data?.permissions ?? []);
    for (const [code, enabled] of permissionOverrides) { if (enabled) selected.add(code); else selected.delete(code); }
    return selected;
  }, [permissionOverrides, role.data?.permissions]);
  const visiblePermissions = useMemo(() => availablePermissions.data?.items.filter((permission) => `${permission.code} ${permission.name} ${permissionLabel(permission.code, permission.name)}`.toLowerCase().includes(permissionSearch.toLowerCase())) ?? [], [availablePermissions.data, permissionSearch]);
  const immutable = Boolean(role.data?.isSystemRole);
  const editable = canWrite && !immutable;
  const save = useMutation({ mutationFn: (values: RoleFormValues) => apiRequest<RoleSummary>(create ? "/api/roles" : `/api/roles/${roleId}`, { method: create ? "POST" : "PUT", body: JSON.stringify(create ? rolePayload(values) : { ...rolePayload(values), version: role.data?.version }) }), onSuccess: async (saved) => { queryClient.setQueryData(["role", saved.id], saved); await queryClient.invalidateQueries({ queryKey: ["roles"] }); if (create) navigate(`/roles/${saved.id}`, { replace: true }); else { form.reset(roleDefaults(saved)); setFeedback("El rol quedó actualizado."); } } });
  const savePermissions = useMutation({ mutationFn: () => { const ids = availablePermissions.data?.items.filter((permission) => selectedPermissions.has(permission.code)).map((permission) => permission.id) ?? []; return apiRequest<RoleSummary>(`/api/roles/${roleId}/permissions`, { method: "PUT", body: JSON.stringify({ permissionIds: ids }) }); }, onSuccess: async (saved) => { queryClient.setQueryData(["role", saved.id], saved); await queryClient.invalidateQueries({ queryKey: ["roles"] }); setPermissionOverrides(new Map()); setFeedback("La matriz de permisos quedó actualizada de forma atómica."); } });
  const changeStatus = useMutation({ mutationFn: () => apiRequest<void>(`/api/roles/${roleId}/${role.data?.isActive ? "deactivate" : "activate"}`, { method: "PATCH" }), onSuccess: async () => { setConfirmStatus(false); setFeedback("El estado del rol quedó actualizado."); await Promise.all([queryClient.invalidateQueries({ queryKey: ["role", roleId] }), queryClient.invalidateQueries({ queryKey: ["roles"] })]); } });
  if (!create && role.isPending) return <PageState title="Cargando rol" busy />;
  if (!create && role.isError) return <PageState title="No pudimos cargar el rol" detail={errorMessage(role.error)} tone="error" />;
  const current = role.data;
  const title = create ? "Nuevo rol" : current?.name ?? "Rol";
  if (create && !canReadApplications) return <PageState tone="forbidden" title="Acceso complementario requerido" detail={needPermission("AUTHCENTER_APPLICATIONS_READ", "elegir la aplicación del nuevo rol")} />;
  return <>
    <Breadcrumbs items={[{ label: "Roles", to: "/roles" }, { label: title }]} />
    <PageHeader eyebrow={immutable ? "Rol de sistema" : "Acceso"} title={title} description={immutable ? "Este rol es de sistema y sólo puede consultarse." : editable ? "Configura identidad, estado y permisos efectivos por aplicación." : "Consulta el rol. Tu acceso es de sólo lectura."} actions={<>{create ? null : <HistoryLink entityName="ApplicationRole" entityId={roleId} />}<Link className="button button--secondary" to="/roles">Volver</Link></>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}<SaveError error={save.error ?? savePermissions.error} onReload={() => { save.reset(); void role.refetch().then((fresh) => { if (fresh.data) form.reset(roleDefaults(fresh.data)); }); }} />
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => save.mutateAsync(values))(event)}><fieldset className="settings-fieldset" disabled={!editable && !create}><section className="settings-panel"><div className="settings-panel__heading"><div><h2>Identidad y alcance</h2><p>Los roles administrativos creados desde la consola nunca pueden convertirse en roles del sistema.</p></div>{current ? <StatusBadge active={current.isActive} /> : null}</div><div className="form-grid"><Field label="Aplicación" error={form.formState.errors.applicationSystemId?.message}><select {...form.register("applicationSystemId")} disabled={!create}><option value="">Selecciona…</option>{applications.data?.items.map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></Field><Field label="Nombre" error={form.formState.errors.name?.message}><input {...form.register("name")} /></Field></div><Field label="Descripción" error={form.formState.errors.description?.message}><textarea {...form.register("description")} rows={3} /></Field></section></fieldset>{(editable || create) ? <div className="form-footer"><Link className="button button--secondary" to="/roles">Cancelar</Link><button className="button" disabled={save.isPending}>{save.isPending ? "Guardando…" : create ? "Crear rol" : "Guardar identidad"}</button></div> : null}</form>
    {current && canReadPermissions ? <section className="settings-panel settings-panel--actions" aria-labelledby="permission-matrix"><div className="settings-panel__heading"><div><h2 id="permission-matrix">Matriz de permisos</h2><p>Sólo aparecen permisos de la aplicación del rol. Los inactivos no pueden asignarse.</p></div><span className="tag">{selectedPermissions.size} seleccionados</span></div><label className="field field--search"><span>Buscar permiso</span><input type="search" value={permissionSearch} onChange={(event) => setPermissionSearch(event.target.value)} placeholder="Código o nombre" /></label>{availablePermissions.isPending ? <p className="muted">Cargando permisos…</p> : <div className="permission-matrix">{visiblePermissions.map((permission) => <label className={`permission-option ${permission.isActive ? "" : "permission-option--inactive"}`} key={permission.id}><input type="checkbox" checked={selectedPermissions.has(permission.code)} disabled={!editable || !permission.isActive} onChange={(event) => setPermissionOverrides((currentMap) => new Map(currentMap).set(permission.code, event.target.checked))} /><span><strong>{permissionLabel(permission.code, permission.name)}{permission.isActive ? "" : " · inactivo"}</strong><small className="mono">{permission.code}</small></span></label>)}</div>}{editable ? <div className="form-footer"><button className="button" type="button" disabled={savePermissions.isPending} onClick={() => savePermissions.mutate()}>{savePermissions.isPending ? "Guardando…" : "Guardar matriz"}</button></div> : null}</section> : current ? <p className="alert alert--info">{needPermission("AUTHCENTER_PERMISSIONS_READ", "consultar la matriz")}</p> : null}
    {current && editable ? <section className="settings-panel settings-panel--actions"><div className="settings-panel__heading"><div><h2>Operación</h2><p>Desactivar el rol elimina su contribución al acceso efectivo.</p></div></div><button className={`button ${current.isActive ? "button--danger-quiet" : "button--secondary"}`} onClick={() => setConfirmStatus(true)}>{current.isActive ? "Desactivar" : "Activar"}</button></section> : null}
    <ConfirmDialog open={confirmStatus} title={`${current?.isActive ? "Desactivar" : "Activar"} rol`} detail="El cambio afectará el acceso efectivo de usuarios y grupos que lo tengan asignado." confirmLabel={current?.isActive ? "Desactivar" : "Activar"} dangerous={Boolean(current?.isActive)} busy={changeStatus.isPending} error={changeStatus.error} onCancel={() => setConfirmStatus(false)} onConfirm={() => changeStatus.mutate()} />
  </>;
}

function Field({ label, error, children }: { label: string; error: string | undefined; children: React.ReactNode }) { return <label className="field"><span>{label}</span>{children}{error ? <span className="field-error">{error}</span> : null}</label>; }
