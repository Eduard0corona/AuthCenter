import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { PagedResult, ScimDiagnostics, ScimRequestLogEntry } from "../../api/types";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { buildQuery, formatDate } from "../../utils/format";
import { describeScimOutcome } from "./provisioning-token";

type Outcome = "" | "failed" | "succeeded";

/**
 * What the SCIM client did with this token: counts, the kinds of failure of the week and each request
 * with its outcome. Payloads are never recorded, so nothing here exposes provisioned data.
 */
export function ScimDiagnosticsPanel({ tokenId }: { tokenId: string }) {
  const [outcome, setOutcome] = useState<Outcome>("");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const diagnostics = useQuery({
    queryKey: ["provisioning-token-diagnostics", tokenId],
    queryFn: ({ signal }) => apiRequest<ScimDiagnostics>(`/api/provisioning-tokens/${tokenId}/diagnostics`, { signal })
  });
  const requests = useQuery({
    queryKey: ["provisioning-token-requests", tokenId, outcome, page, pageSize],
    queryFn: ({ signal }) => apiRequest<PagedResult<ScimRequestLogEntry>>(`/api/provisioning-tokens/${tokenId}/requests?${buildQuery({ outcome: outcome || null, page, pageSize })}`, { signal })
  });
  const summary = diagnostics.data;

  return <section className="settings-panel settings-panel--actions" aria-labelledby="provisioning-token-diagnostics">
    <div className="settings-panel__heading">
      <div><h2 id="provisioning-token-diagnostics">Diagnóstico SCIM</h2><p>Solicitudes recibidas con esta credencial y cómo terminaron. No se guarda el contenido de las solicitudes.</p></div>
      <button className="button button--small button--secondary" type="button" onClick={() => { void diagnostics.refetch(); void requests.refetch(); }}>Actualizar</button>
    </div>
    {diagnostics.isPending ? <PageState title="Cargando diagnóstico" busy /> : null}
    {diagnostics.isError ? <PageState title="No pudimos cargar el diagnóstico" detail={errorMessage(diagnostics.error)} tone="error" action={<button className="button" type="button" onClick={() => void diagnostics.refetch()}>Reintentar</button>} /> : null}
    {summary ? <>
      <dl className="profile-summary">
        <div><dt>Últimas 24 horas</dt><dd>{summary.last24Hours.total} solicitudes, {summary.last24Hours.failed} fallidas</dd></div>
        <div><dt>Últimos 7 días</dt><dd>{summary.last7Days.total} solicitudes, {summary.last7Days.failed} fallidas</dd></div>
        <div><dt>Última correcta</dt><dd>{formatDate(summary.lastSucceededAt)}</dd></div>
        <div><dt>Último error</dt><dd>{formatDate(summary.lastFailedAt)}</dd></div>
      </dl>
      {summary.failures.length > 0
        ? <div className="data-table" tabIndex={0} role="region" aria-label="Errores de la semana, desplazamiento horizontal"><table><caption className="sr-only">Errores de los últimos 7 días</caption><thead><tr><th>Error</th><th>Veces</th><th>Último</th><th>Detalle</th></tr></thead><tbody>{summary.failures.map((failure) => <tr key={`${failure.statusCode}-${failure.scimType ?? ""}`}><td><span className="tag tag--removed">{describeScimOutcome(failure.statusCode, failure.scimType)}</span></td><td>{failure.count}</td><td>{formatDate(failure.lastAt)}</td><td>{failure.lastDetail ?? "—"}</td></tr>)}</tbody></table></div>
        : summary.last7Days.total > 0 ? <p className="alert alert--success">Sin errores en los últimos 7 días.</p> : <p className="muted">La credencial no recibió solicitudes en los últimos 7 días.</p>}
    </> : null}
    <div className="toolbar" role="group" aria-label="Filtro de solicitudes">
      <label className="field"><span>Resultado</span><select value={outcome} onChange={(event) => { setOutcome(event.target.value as Outcome); setPage(1); }}><option value="">Todas</option><option value="failed">Fallidas</option><option value="succeeded">Correctas</option></select></label>
    </div>
    {requests.isPending ? <PageState title="Cargando solicitudes" busy /> : null}
    {requests.isError ? <PageState title="No pudimos cargar las solicitudes" detail={errorMessage(requests.error)} tone="error" action={<button className="button" type="button" onClick={() => void requests.refetch()}>Reintentar</button>} /> : null}
    {requests.data && requests.data.items.length === 0 ? <p className="muted">No hay solicitudes con este filtro.</p> : null}
    {requests.data?.items.length ? <>
      <div className="data-table" tabIndex={0} role="region" aria-label="Solicitudes SCIM, desplazamiento horizontal"><table><caption className="sr-only">Solicitudes SCIM</caption><thead><tr><th>Fecha</th><th>Solicitud</th><th>Resultado</th><th>Detalle</th><th>Duración</th><th>Traza</th></tr></thead><tbody>{requests.data.items.map((request) => <tr key={request.id}>
        <td>{formatDate(request.createdAt)}</td>
        <td><span className="mono">{request.method} {request.path}</span></td>
        <td><span className={`tag ${request.statusCode >= 400 ? "tag--removed" : "tag--added"}`}>{describeScimOutcome(request.statusCode, request.scimType)}</span></td>
        <td>{request.detail ?? "—"}</td>
        <td>{request.durationMs} ms</td>
        <td><span className="mono">{request.traceId ?? "—"}</span></td>
      </tr>)}</tbody></table></div>
      <Pagination page={requests.data.page} pageSize={requests.data.pageSize} totalCount={requests.data.totalCount} totalPages={requests.data.totalPages} onPageChange={setPage} onPageSizeChange={(value) => { setPageSize(value); setPage(1); }} />
    </> : null}
  </section>;
}
