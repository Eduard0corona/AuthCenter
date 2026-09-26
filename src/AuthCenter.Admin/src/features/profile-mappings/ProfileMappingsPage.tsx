import { useQuery } from "@tanstack/react-query";
import { Link, useSearchParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, PagedResult, ProfileMapping } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { buildQuery, formatDate } from "../../utils/format";

export default function ProfileMappingsPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_USERS_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const [params, setParams] = useSearchParams();
  const page = Math.max(Number(params.get("page") ?? 1), 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const applicationId = params.get("applicationId") ?? "";
  const active = params.get("active") ?? "";
  const applications = useQuery({
    queryKey: ["applications", "profile-mapping-filter"],
    enabled: canReadApplications,
    queryFn: ({ signal }) => fetchAllAsPage<ApplicationSummary>("/api/applications", signal)
  });
  const mappings = useQuery({
    queryKey: ["profile-mappings", page, pageSize, applicationId, active],
    queryFn: ({ signal }) => apiRequest<PagedResult<ProfileMapping>>(`/api/lifecycle/profile-mappings?${buildQuery({
      page, pageSize, applicationSystemId: applicationId || null, isActive: active === "" ? null : active === "true"
    })}`, { signal })
  });

  function update(name: string, value: string): void {
    setParams((current) => {
      const next = new URLSearchParams(current);
      if (value) next.set(name, value); else next.delete(name);
      if (name !== "page") next.set("page", "1");
      return next;
    });
  }

  return <>
    <PageHeader eyebrow="Lifecycle" title="Profile mappings" description="Define qué atributos SCIM alimentan el perfil universal y simula la transformación antes de activar cada regla." actions={canWrite ? <Link className="button" to="/profile-mappings/new">Nuevo mapping</Link> : undefined} />
    <section className="toolbar" aria-label="Filtros de profile mappings">
      <label className="field"><span>Aplicación</span><select value={applicationId} onChange={(event) => update("applicationId", event.target.value)} disabled={!canReadApplications}><option value="">Todas</option>{applications.data?.items.map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></label>
      <label className="field"><span>Estado</span><select value={active} onChange={(event) => update("active", event.target.value)}><option value="">Todos</option><option value="true">Activos</option><option value="false">Inactivos</option></select></label>
    </section>
    {mappings.isPending ? <PageState title="Cargando profile mappings" busy /> : null}
    {mappings.isError ? <PageState title="No pudimos cargar los profile mappings" detail={errorMessage(mappings.error)} tone="error" action={<button className="button" onClick={() => void mappings.refetch()}>Reintentar</button>} /> : null}
    {mappings.data && mappings.data.items.length === 0 ? <PageState title="No hay profile mappings" detail="Ajusta los filtros o crea el primer mapping SCIM hacia el perfil universal." /> : null}
    {mappings.data?.items.length ? <>
      <div className="data-table" tabIndex={0} role="region" aria-label="Profile mappings, desplazamiento horizontal"><table><caption className="sr-only">Profile mappings</caption><thead><tr><th>Origen SCIM</th><th>Atributo destino</th><th>Aplicación</th><th>Autoritativo</th><th>Creado</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{mappings.data.items.map((mapping) => <tr key={mapping.id}><td><strong className="mono">{mapping.sourcePath}</strong><span className="cell-detail">{mapping.sourceSystem}</span></td><td><span className="mono">{mapping.targetAttributeName}</span></td><td>{mapping.applicationName}</td><td>{mapping.isAuthoritative ? "Sí" : "No"}</td><td>{formatDate(mapping.createdAt)}</td><td><StatusBadge active={mapping.isActive} /></td><td className="table-action"><Link className="button button--small button--secondary" to={`/profile-mappings/${mapping.id}`}>{canWrite ? "Editar" : "Consultar"}</Link></td></tr>)}</tbody></table></div>
      <Pagination page={mappings.data.page} pageSize={mappings.data.pageSize} totalCount={mappings.data.totalCount} totalPages={mappings.data.totalPages} onPageChange={(value) => update("page", String(value))} onPageSizeChange={(value) => update("pageSize", String(value))} />
    </> : null}
  </>;
}

