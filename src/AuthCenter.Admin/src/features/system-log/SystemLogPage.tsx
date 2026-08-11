import { useQuery } from "@tanstack/react-query";
import { useSearchParams } from "react-router-dom";
import { apiRequest, ApiError } from "../../api/client";
import type { AuditLogEntry, PagedResult } from "../../api/types";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { buildQuery, formatDate } from "../../utils/format";

export default function SystemLogPage() {
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const log = useQuery({
    queryKey: ["system-log", params.toString()],
    queryFn: ({ signal }) => apiRequest<PagedResult<AuditLogEntry>>(`/api/audit-logs?${buildQuery({
      page,
      pageSize,
      action: params.get("action"),
      applicationCode: params.get("application"),
      traceId: params.get("trace"),
      fromUtc: params.get("from") ? new Date(`${params.get("from")}T00:00:00Z`).toISOString() : null,
      toUtc: params.get("to") ? new Date(`${params.get("to")}T23:59:59Z`).toISOString() : null
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

  return (
    <>
      <PageHeader eyebrow="Operación" title="System Log" description="Filtra eventos administrativos por acción, aplicación, trace ID y fecha." />
      <section className="toolbar toolbar--wide" aria-label="Filtros de System Log">
        <label className="field"><span>Acción</span><input value={params.get("action") ?? ""} onChange={(event) => update("action", event.target.value)} placeholder="USER_…" /></label>
        <label className="field"><span>Aplicación</span><input value={params.get("application") ?? ""} onChange={(event) => update("application", event.target.value)} placeholder="AUTHCENTER" /></label>
        <label className="field"><span>Trace ID</span><input value={params.get("trace") ?? ""} onChange={(event) => update("trace", event.target.value)} /></label>
        <label className="field"><span>Desde</span><input type="date" value={params.get("from") ?? ""} onChange={(event) => update("from", event.target.value)} /></label>
        <label className="field"><span>Hasta</span><input type="date" value={params.get("to") ?? ""} onChange={(event) => update("to", event.target.value)} /></label>
      </section>
      {log.isPending ? <PageState title="Cargando eventos" busy /> : null}
      {log.isError ? <PageState title="No pudimos cargar System Log" detail={message(log.error)} tone="error" action={<button className="button" type="button" onClick={() => void log.refetch()}>Reintentar</button>} /> : null}
      {log.data && log.data.items.length === 0 ? <PageState title="No hay eventos" detail="No encontramos resultados con estos filtros." /> : null}
      {log.data && log.data.items.length > 0 ? <><div className="data-table" tabIndex={0} role="region" aria-label="Eventos del System Log, desplazamiento horizontal"><table><caption className="sr-only">Eventos del System Log</caption><thead><tr><th scope="col">Fecha</th><th scope="col">Acción</th><th scope="col">Aplicación</th><th scope="col">Entidad</th><th scope="col">Trace ID</th></tr></thead><tbody>{log.data.items.map((entry) => <tr key={entry.id}><td>{formatDate(entry.createdAt)}</td><td><code>{entry.action}</code></td><td>{entry.applicationCode ?? "—"}</td><td>{[entry.entityName, entry.entityId].filter(Boolean).join(" · ") || "—"}</td><td>{entry.traceId ? <code className="trace-id">{entry.traceId}</code> : "—"}</td></tr>)}</tbody></table></div><Pagination page={log.data.page} pageSize={log.data.pageSize} totalCount={log.data.totalCount} totalPages={log.data.totalPages} onPageChange={(value) => update("page", String(value))} onPageSizeChange={(value) => update("pageSize", String(value))} /></> : null}
    </>
  );
}

function message(error: unknown): string { return error instanceof ApiError ? error.message : "Ocurrió un error inesperado."; }
