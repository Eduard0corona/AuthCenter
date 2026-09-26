import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useCallback } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useApplicationsCatalog } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApiResource, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { DebouncedTextField } from "../../components/DebouncedTextField";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { buildQuery } from "../../utils/format";

export default function ApiResourcesPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_OAUTH_CLIENTS_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const search = params.get("search") ?? "";
  const applicationSystemId = params.get("application") ?? "";
  const applications = useApplicationsCatalog(canReadApplications);
  const resources = useQuery({
    queryKey: ["api-resources", search, applicationSystemId, page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => apiRequest<PagedResult<ApiResource>>(`/api/api-resources?${buildQuery({ page, pageSize, search, applicationSystemId })}`, { signal })
  });
  const update = useCallback((name: string, value: string, replace = true) => setParams((current) => {
    const next = new URLSearchParams(current);
    if (value) next.set(name, value); else next.delete(name);
    if (name !== "page") next.delete("page");
    return next;
  }, { replace }), [setParams]);
  const commitSearch = useCallback((value: string) => update("search", value), [update]);

  return (
    <>
      <PageHeader
        eyebrow="Aplicaciones"
        title="Recursos de API"
        description="APIs que confían en AuthCenter (RFC 8707): su identificador es la audiencia del token y sus scopes son lo que los clientes pueden pedir."
        actions={canWrite && canReadApplications ? <Link className="button" to="/api-resources/new">Nuevo API</Link> : undefined}
      />
      <section className="toolbar" aria-label="Filtros de recursos de API">
        <DebouncedTextField label="Buscar" value={search} onCommit={commitSearch} placeholder="Nombre o identificador" />
        {canReadApplications ? <label className="field"><span>Aplicación</span><select value={applicationSystemId} onChange={(event) => update("application", event.target.value, false)}><option value="">Todas</option>{applications.data?.map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></label> : null}
      </section>
      {resources.isPending ? <PageState title="Cargando APIs" busy /> : null}
      {resources.isError ? <PageState title="No pudimos cargar los recursos de API" detail={errorMessage(resources.error)} tone="error" action={<button className="button" type="button" onClick={() => void resources.refetch()}>Reintentar</button>} /> : null}
      {resources.data && resources.data.items.length === 0 ? <PageState title="Sin recursos de API" detail={search || applicationSystemId ? "No encontramos APIs con estos filtros." : "Registra un API para emitir tokens con su audiencia y scopes."} /> : null}
      {resources.data && resources.data.items.length > 0 ? <>
        <div className="data-table" tabIndex={0} role="region" aria-label="Recursos de API, desplazamiento horizontal" aria-busy={resources.isFetching || undefined}>
          <table>
            <caption className="sr-only">Recursos de API</caption>
            <thead><tr><th scope="col">API</th><th scope="col">Aplicación</th><th scope="col">Scopes</th><th scope="col">Estado</th><th scope="col"><span className="sr-only">Acciones</span></th></tr></thead>
            <tbody>{resources.data.items.map((resource) => <tr key={resource.id}>
              <td><strong>{resource.displayName}</strong><span className="cell-detail mono">{resource.identifier}</span></td>
              <td>{resource.applicationName}<span className="cell-detail">{resource.applicationCode}</span></td>
              <td>{resource.scopes.slice(0, 4).map((scope) => <span className="tag mono" key={scope.id}>{scope.name}</span>)}{resource.scopes.length > 4 ? <span className="tag">+{resource.scopes.length - 4}</span> : null}</td>
              <td><StatusBadge active={resource.isActive} /></td>
              <td className="table-action"><Link className="button button--small button--secondary" to={`/api-resources/${resource.id}`}>Abrir<span className="sr-only"> {resource.displayName}</span></Link></td>
            </tr>)}</tbody>
          </table>
        </div>
        <Pagination page={resources.data.page} pageSize={resources.data.pageSize} totalCount={resources.data.totalCount} totalPages={resources.data.totalPages} onPageChange={(value) => update("page", String(value), false)} onPageSizeChange={(value) => update("pageSize", String(value), false)} />
      </> : null}
    </>
  );
}
