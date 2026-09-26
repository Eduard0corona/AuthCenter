import { useQuery } from "@tanstack/react-query";
import { Link, useSearchParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, PagedResult, RoleSummary } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { buildQuery } from "../../utils/format";

export default function RolesPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_ROLES_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const applicationId = params.get("applicationId") ?? "";
  const applications = useQuery({ queryKey: ["applications", "role-filter"], enabled: canReadApplications, queryFn: ({ signal }) => fetchAllAsPage<ApplicationSummary>("/api/applications", signal) });
  const roles = useQuery({
    queryKey: ["roles", page, pageSize, applicationId],
    queryFn: ({ signal }) => apiRequest<PagedResult<RoleSummary>>(`/api/roles?${buildQuery({ page, pageSize, applicationSystemId: applicationId || null })}`, { signal })
  });
  const appNames = new Map(applications.data?.items.map((application) => [application.id, application.name]) ?? []);

  function update(name: string, value: string): void {
    setParams((current) => { const next = new URLSearchParams(current); if (value) next.set(name, value); else next.delete(name); if (name !== "page") next.set("page", "1"); return next; });
  }

  return <>
    <PageHeader eyebrow="Acceso" title="Roles" description="Administra roles con alcance explícito por aplicación y abre su matriz de permisos." actions={canWrite ? <Link className="button" to="/roles/new">Nuevo rol</Link> : undefined} />
    {canReadApplications ? <section className="toolbar" aria-label="Filtros de roles"><label className="field"><span>Aplicación</span><select value={applicationId} onChange={(event) => update("applicationId", event.target.value)}><option value="">Todas</option>{applications.data?.items.map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></label></section> : null}
    {roles.isPending ? <PageState title="Cargando roles" busy /> : null}
    {roles.isError ? <PageState title="No pudimos cargar los roles" detail={errorMessage(roles.error)} tone="error" action={<button className="button" onClick={() => void roles.refetch()}>Reintentar</button>} /> : null}
    {roles.data && roles.data.items.length === 0 ? <PageState title="No hay roles" detail="Crea un rol para esta aplicación." /> : null}
    {roles.data?.items.length ? <><div className="data-table" tabIndex={0} role="region" aria-label="Roles, desplazamiento horizontal"><table><caption className="sr-only">Roles</caption><thead><tr><th>Rol</th><th>Aplicación</th><th>Tipo</th><th>Permisos</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{roles.data.items.map((role) => <tr key={role.id}><td><strong>{role.name}</strong><span className="cell-detail">{role.description ?? "Sin descripción"}</span></td><td>{role.applicationSystemId ? appNames.get(role.applicationSystemId) ?? "Aplicación" : "Global"}</td><td>{role.isSystemRole ? <span className="tag">Sistema</span> : "Personalizado"}</td><td>{role.permissions.length}</td><td><StatusBadge active={role.isActive} /></td><td className="table-action"><Link className="button button--small button--secondary" to={`/roles/${role.id}`}>{canWrite && !role.isSystemRole ? "Configurar" : "Consultar"}</Link></td></tr>)}</tbody></table></div><Pagination page={roles.data.page} pageSize={roles.data.pageSize} totalCount={roles.data.totalCount} totalPages={roles.data.totalPages} onPageChange={(value) => update("page", String(value))} onPageSizeChange={(value) => update("pageSize", String(value))} /></> : null}
  </>;
}

