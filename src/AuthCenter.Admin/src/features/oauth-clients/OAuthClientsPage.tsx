import { useQuery } from "@tanstack/react-query";
import { Link, useSearchParams } from "react-router-dom";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, OAuthClientSummary, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { buildQuery } from "../../utils/format";

export default function OAuthClientsPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_OAUTH_CLIENTS_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const search = params.get("search") ?? "";
  const applicationId = params.get("applicationId") ?? "";
  const clientType = params.get("clientType") ?? "";
  const status = params.get("status") ?? "";
  const applications = useQuery({
    queryKey: ["applications", "oauth-client-filter"],
    enabled: canReadApplications,
    queryFn: ({ signal }) => apiRequest<PagedResult<ApplicationSummary>>("/api/applications?page=1&pageSize=100", { signal })
  });
  const clients = useQuery({
    queryKey: ["oauth-clients", page, pageSize, search, applicationId, clientType, status],
    queryFn: ({ signal }) => apiRequest<PagedResult<OAuthClientSummary>>(`/api/oauth/clients?${buildQuery({
      page,
      pageSize,
      search: search || null,
      applicationSystemId: applicationId || null,
      clientType: clientType || null,
      isActive: status === "active" ? true : status === "inactive" ? false : null
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
    <PageHeader eyebrow="Integraciones" title="OAuth clients" description="Administra clientes, redirects exactos, grants, scopes y credenciales de integración." actions={canWrite ? <Link className="button" to="/oauth-clients/new">Nuevo client</Link> : undefined} />
    <section className="toolbar toolbar--wide" aria-label="Filtros de OAuth clients">
      <label className="field"><span>Buscar</span><input type="search" value={search} onChange={(event) => update("search", event.target.value)} placeholder="Nombre o client ID" /></label>
      {canReadApplications ? <label className="field"><span>Aplicación</span><select value={applicationId} onChange={(event) => update("applicationId", event.target.value)}><option value="">Todas</option>{applications.data?.items.map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></label> : null}
      <label className="field"><span>Tipo</span><select value={clientType} onChange={(event) => update("clientType", event.target.value)}><option value="">Todos</option><option value="0">Confidencial</option><option value="1">Público</option></select></label>
      <label className="field"><span>Estado</span><select value={status} onChange={(event) => update("status", event.target.value)}><option value="">Todos</option><option value="active">Activos</option><option value="inactive">Inactivos</option></select></label>
    </section>
    {clients.isPending ? <PageState title="Cargando OAuth clients" busy /> : null}
    {clients.isError ? <PageState title="No pudimos cargar los OAuth clients" detail={errorMessage(clients.error)} tone="error" action={<button className="button" onClick={() => void clients.refetch()}>Reintentar</button>} /> : null}
    {clients.data && clients.data.items.length === 0 ? <PageState title="No hay OAuth clients" detail="Ajusta los filtros o registra el primer cliente." /> : null}
    {clients.data?.items.length ? <>
      <div className="data-table" tabIndex={0} role="region" aria-label="OAuth clients, desplazamiento horizontal"><table><caption className="sr-only">OAuth clients</caption><thead><tr><th>Cliente</th><th>Aplicación</th><th>Tipo</th><th>Grants</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{clients.data.items.map((client) => <tr key={client.id}><td><strong>{client.displayName}</strong><span className="cell-detail mono">{client.clientId}</span></td><td>{client.applicationName}</td><td>{client.clientType === 0 ? "Confidencial" : "Público"}</td><td><span className="cell-detail">{client.grantTypes.join(", ")}</span></td><td><StatusBadge active={client.isActive} /></td><td className="table-action"><Link className="button button--small button--secondary" to={`/oauth-clients/${encodeURIComponent(client.clientId)}`}>{canWrite ? "Configurar" : "Consultar"}</Link></td></tr>)}</tbody></table></div>
      <Pagination page={clients.data.page} pageSize={clients.data.pageSize} totalCount={clients.data.totalCount} totalPages={clients.data.totalPages} onPageChange={(value) => update("page", String(value))} onPageSizeChange={(value) => update("pageSize", String(value))} />
    </> : null}
  </>;
}

