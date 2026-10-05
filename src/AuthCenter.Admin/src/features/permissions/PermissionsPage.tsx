import { useQuery } from "@tanstack/react-query";
import { Link, useSearchParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, PagedResult, PermissionSummary } from "../../api/types";
import { permissionLabel } from "../../auth/permissions";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { buildQuery } from "../../utils/format";

export default function PermissionsPage() {
  const { permissions: sessionPermissions } = useSession();
  const canWrite = sessionPermissions.has("AUTHCENTER_PERMISSIONS_WRITE");
  const canReadApplications = sessionPermissions.has("AUTHCENTER_APPLICATIONS_READ");
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const applicationId = params.get("applicationId") ?? "";
  const applications = useQuery({ queryKey: ["applications", "permission-filter"], enabled: canReadApplications, queryFn: ({ signal }) => fetchAllAsPage<ApplicationSummary>("/api/applications", signal) });
  const permissions = useQuery({ queryKey: ["permissions", page, pageSize, applicationId], queryFn: ({ signal }) => apiRequest<PagedResult<PermissionSummary>>(applicationId ? `/api/applications/${applicationId}/permissions?${buildQuery({ page, pageSize })}` : `/api/permissions?${buildQuery({ page, pageSize })}`, { signal }) });
  const appNames = new Map(applications.data?.items.map((application) => [application.id, application.name]) ?? []);
  function update(name: string, value: string): void { setParams((current) => { const next = new URLSearchParams(current); if (value) next.set(name, value); else next.delete(name); if (name !== "page") next.set("page", "1"); return next; }); }

  return <>
    <PageHeader eyebrow="Acceso" title="Permisos" description="Cada permiso nombra algo que una persona puede hacer en una aplicación. Agrúpalos en roles para darlos a usuarios y grupos." actions={canWrite ? <Link className="button" to="/permissions/new">Nuevo permiso</Link> : undefined} />
    {canReadApplications ? <section className="toolbar" aria-label="Filtros de permisos"><label className="field"><span>Aplicación</span><select value={applicationId} onChange={(event) => update("applicationId", event.target.value)}><option value="">Todas</option>{applications.data?.items.map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></label></section> : null}
    {permissions.isPending ? <PageState title="Cargando permisos" busy /> : null}
    {permissions.isError ? <PageState title="No pudimos cargar los permisos" detail={errorMessage(permissions.error)} tone="error" /> : null}
    {permissions.data?.items.length ? <><div className="data-table" tabIndex={0} role="region" aria-label="Permisos, desplazamiento horizontal"><table><caption className="sr-only">Permisos</caption><thead><tr><th>Permiso</th><th>Código</th><th>Aplicación</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{permissions.data.items.map((permission) => <tr key={permission.id}><td><strong>{permissionLabel(permission.code, permission.name)}</strong><span className="cell-detail">{permission.description ?? "Sin descripción"}</span></td><td className="mono">{permission.code}</td><td>{appNames.get(permission.applicationSystemId) ?? "Aplicación"}</td><td><StatusBadge active={permission.isActive} /></td><td className="table-action"><Link className="button button--small button--secondary" to={`/permissions/${permission.id}`}>{canWrite ? "Configurar" : "Consultar"}</Link></td></tr>)}</tbody></table></div><Pagination page={permissions.data.page} pageSize={permissions.data.pageSize} totalCount={permissions.data.totalCount} totalPages={permissions.data.totalPages} onPageChange={(value) => update("page", String(value))} onPageSizeChange={(value) => update("pageSize", String(value))} /></> : null}
    {permissions.data && permissions.data.items.length === 0 ? <PageState title="No hay permisos" detail="Crea una capacidad para esta aplicación." /> : null}
  </>;
}

