import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useCallback, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { AuditLogEntry, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { DebouncedTextField } from "../../components/DebouncedTextField";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { useModalDialog } from "../../hooks/useModalDialog";
import { buildQuery, formatDate } from "../../utils/format";
import { dayBoundary, entityLabel, entityPath, formatMetadata } from "./entities";

const TEXT_FILTERS = ["action", "application", "entity", "entityId", "user", "trace", "from", "to"] as const;
type FilterName = (typeof TEXT_FILTERS)[number];

interface ExportFeedback { tone: "success" | "error" | "info"; text: string; }

const upper = (value: string) => value.trim().toUpperCase();

export default function SystemLogPage() {
  const { permissions } = useSession();
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const [selected, setSelected] = useState<AuditLogEntry | null>(null);
  const [exporting, setExporting] = useState(false);
  const [exportFeedback, setExportFeedback] = useState<ExportFeedback | null>(null);
  const filterQuery = buildQuery({
    action: params.get("action"),
    applicationCode: params.get("application"),
    entityName: params.get("entity"),
    entityId: params.get("entityId"),
    userId: params.get("user"),
    traceId: params.get("trace"),
    fromUtc: dayBoundary(params.get("from"), "start"),
    toUtc: dayBoundary(params.get("to"), "end")
  });
  const activeFilters = TEXT_FILTERS.filter((name) => params.get(name));
  const log = useQuery({
    queryKey: ["system-log", filterQuery, page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => apiRequest<PagedResult<AuditLogEntry>>(`/api/audit-logs?${filterQuery}${filterQuery ? "&" : ""}${buildQuery({ page, pageSize })}`, { signal })
  });

  const update = useCallback((values: Partial<Record<FilterName | "page" | "pageSize", string>>, replace = true) => {
    setParams((current) => {
      const next = new URLSearchParams(current);
      for (const [name, value] of Object.entries(values)) {
        if (value) next.set(name, value); else next.delete(name);
      }
      if (!("page" in values)) next.delete("page");
      return next;
    }, { replace });
  }, [setParams]);
  const commits = useMemo(() => Object.fromEntries(TEXT_FILTERS.map((name) => [name, (value: string) => update({ [name]: value })])) as Record<FilterName, (value: string) => void>, [update]);

  function showOnly(values: Partial<Record<FilterName, string>>): void {
    setSelected(null);
    setParams(() => {
      const next = new URLSearchParams();
      for (const [name, value] of Object.entries(values)) if (value) next.set(name, value);
      return next;
    });
  }

  async function exportCsv(): Promise<void> {
    setExporting(true);
    setExportFeedback(null);
    try {
      const response = await fetch(`/api/audit-logs/export?${filterQuery}`, { credentials: "same-origin" });
      if (!response.ok) throw new Error(response.status === 403 ? "No tienes permiso para exportar el registro de actividad." : `La exportación falló (${response.status}).`);
      const total = Number(response.headers.get("X-Total-Count") ?? "0");
      const truncated = response.headers.get("X-AuthCenter-Export-Truncated") === "true";
      const url = URL.createObjectURL(await response.blob());
      const link = document.createElement("a");
      link.href = url;
      link.download = fileName(response.headers.get("content-disposition")) ?? "authcenter-system-log.csv";
      link.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1_000);
      setExportFeedback(truncated
        ? { tone: "info", text: `Se exportaron los 10 000 eventos más recientes de ${total.toLocaleString("es-MX")}. Acota las fechas para obtener el resto.` }
        : { tone: "success", text: `Se exportaron ${total.toLocaleString("es-MX")} eventos.` });
    } catch (error) {
      setExportFeedback({ tone: "error", text: errorMessage(error) });
    } finally {
      setExporting(false);
    }
  }

  return (
    <>
      <PageHeader
        eyebrow="Operación"
        title="Registro de actividad"
        description="Eventos de seguridad y administración: quién hizo qué, sobre qué recurso y desde dónde."
        actions={<button className="button button--secondary" type="button" onClick={() => void exportCsv()} disabled={exporting}>{exporting ? "Exportando…" : "Exportar CSV"}</button>}
      />
      <section className="toolbar toolbar--wide" aria-label="Filtros del registro de actividad">
        <DebouncedTextField label="Acción" value={params.get("action") ?? ""} onCommit={commits.action} placeholder="LOGIN_FAILED" normalize={upper} />
        <DebouncedTextField label="Aplicación" value={params.get("application") ?? ""} onCommit={commits.application} placeholder="AUTHCENTER" normalize={upper} />
        <DebouncedTextField label="Tipo de entidad" value={params.get("entity") ?? ""} onCommit={commits.entity} placeholder="ApplicationUser" />
        <DebouncedTextField label="ID de entidad" value={params.get("entityId") ?? ""} onCommit={commits.entityId} />
        <DebouncedTextField label="ID del actor" value={params.get("user") ?? ""} onCommit={commits.user} />
        <DebouncedTextField label="Referencia (trace ID)" value={params.get("trace") ?? ""} onCommit={commits.trace} />
        <label className="field"><span>Desde (hora local)</span><input type="date" value={params.get("from") ?? ""} onChange={(event) => update({ from: event.target.value }, false)} /></label>
        <label className="field"><span>Hasta (hora local)</span><input type="date" value={params.get("to") ?? ""} onChange={(event) => update({ to: event.target.value }, false)} /></label>
      </section>
      {activeFilters.length > 0 ? <p className="filter-summary"><span>{activeFilters.length === 1 ? "1 filtro activo" : `${activeFilters.length} filtros activos`}</span><button className="button button--small button--secondary" type="button" onClick={() => showOnly({})}>Quitar filtros</button></p> : null}
      {exportFeedback ? <p className={`alert alert--${exportFeedback.tone}`} role={exportFeedback.tone === "error" ? "alert" : "status"}>{exportFeedback.text}</p> : null}
      {log.isPending ? <PageState title="Cargando eventos" busy /> : null}
      {log.isError ? <PageState title="No pudimos cargar el registro de actividad" detail={errorMessage(log.error)} tone="error" action={<button className="button" type="button" onClick={() => void log.refetch()}>Reintentar</button>} /> : null}
      {log.data && log.data.items.length === 0 ? <PageState title="No hay eventos" detail="No encontramos resultados con estos filtros." /> : null}
      {log.data && log.data.items.length > 0 ? <>
        <div className="data-table" tabIndex={0} role="region" aria-label="Eventos del registro de actividad, desplazamiento horizontal" aria-busy={log.isFetching || undefined}>
          <table>
            <caption className="sr-only">Eventos del registro de actividad</caption>
            <thead><tr><th scope="col">Fecha</th><th scope="col">Acción</th><th scope="col">Actor</th><th scope="col">Aplicación</th><th scope="col">Entidad</th><th scope="col"><span className="sr-only">Detalle</span></th></tr></thead>
            <tbody>{log.data.items.map((entry) => {
              const target = entityPath(entry.entityName, entry.entityId, permissions);
              return <tr key={entry.id}>
                <td>{formatDate(entry.createdAt)}</td>
                <td><code>{entry.action}</code></td>
                <td>{entry.userId ? <><span>{entry.userName ?? entry.userEmail ?? "Cuenta eliminada"}</span>{entry.userEmail && entry.userName ? <span className="cell-detail">{entry.userEmail}</span> : null}</> : <span className="muted">Sistema o anónimo</span>}</td>
                <td>{entry.applicationCode ?? "—"}</td>
                <td>{entry.entityName ? <>{target ? <Link to={target}>{entityLabel(entry.entityName)}</Link> : <span>{entityLabel(entry.entityName)}</span>}{entry.entityId ? <span className="cell-detail mono">{entry.entityId}</span> : null}</> : "—"}</td>
                <td className="table-action"><button className="button button--small button--secondary" type="button" onClick={() => setSelected(entry)}>Detalle<span className="sr-only"> del evento {entry.action} del {formatDate(entry.createdAt)}</span></button></td>
              </tr>;
            })}</tbody>
          </table>
        </div>
        <Pagination page={log.data.page} pageSize={log.data.pageSize} totalCount={log.data.totalCount} totalPages={log.data.totalPages} onPageChange={(value) => update({ page: String(value) }, false)} onPageSizeChange={(value) => update({ pageSize: String(value) }, false)} />
      </> : null}
      <EventDetailDialog entry={selected} onClose={() => setSelected(null)} onShowOnly={showOnly} canOpen={(entry) => entityPath(entry.entityName, entry.entityId, permissions)} canOpenActor={permissions.has("AUTHCENTER_USERS_READ")} />
    </>
  );
}

interface EventDetailDialogProps {
  entry: AuditLogEntry | null;
  onClose: () => void;
  onShowOnly: (values: Partial<Record<FilterName, string>>) => void;
  canOpen: (entry: AuditLogEntry) => string | null;
  canOpenActor: boolean;
}

function EventDetailDialog({ entry, onClose, onShowOnly, canOpen, canOpenActor }: EventDetailDialogProps) {
  const dialog = useModalDialog(entry !== null);
  const metadata = formatMetadata(entry?.metadataJson ?? null);
  const target = entry ? canOpen(entry) : null;
  return (
    <dialog ref={dialog} className="dialog dialog--wide" aria-labelledby="event-detail-title" onCancel={(event) => { event.preventDefault(); onClose(); }}>
      {entry ? <>
        <div className="dialog__content">
          <p className="eyebrow">Evento del registro de actividad</p>
          <h2 id="event-detail-title"><code>{entry.action}</code></h2>
          <dl className="detail-list">
            <div><dt>Fecha</dt><dd>{formatDate(entry.createdAt)} <span className="muted mono">{entry.createdAt}</span></dd></div>
            <div><dt>Actor</dt><dd>{entry.userId ? <>{entry.userName ?? entry.userEmail ?? "Cuenta eliminada"}{entry.userEmail ? <span className="cell-detail">{entry.userEmail}</span> : null}<span className="cell-detail mono">{entry.userId}</span></> : "Sistema o anónimo"}</dd></div>
            <div><dt>Aplicación</dt><dd>{entry.applicationCode ?? "—"}</dd></div>
            <div><dt>Entidad</dt><dd>{entry.entityName ? <>{entityLabel(entry.entityName)} <span className="muted">({entry.entityName})</span>{entry.entityId ? <span className="cell-detail mono">{entry.entityId}</span> : null}</> : "—"}</dd></div>
            <div><dt>Dirección IP</dt><dd className="mono">{entry.ipAddress ?? "—"}</dd></div>
            <div><dt>Agente de usuario</dt><dd>{entry.userAgent ?? "—"}</dd></div>
            <div><dt>Referencia (trace ID)</dt><dd className="mono">{entry.traceId ?? "—"}</dd></div>
            <div><dt>ID del evento</dt><dd className="mono">{entry.id}</dd></div>
          </dl>
          <h3>Metadatos</h3>
          {metadata ? <pre className="mono code-block">{metadata}</pre> : <p className="muted">El evento no registró metadatos.</p>}
          <div className="button-row">
            {entry.userId ? <button className="button button--small button--secondary" type="button" onClick={() => onShowOnly({ user: entry.userId ?? "" })}>Eventos de este actor</button> : null}
            {entry.entityName && entry.entityId ? <button className="button button--small button--secondary" type="button" onClick={() => onShowOnly({ entity: entry.entityName ?? "", entityId: entry.entityId ?? "" })}>Historial de esta entidad</button> : null}
            {entry.traceId ? <button className="button button--small button--secondary" type="button" onClick={() => onShowOnly({ trace: entry.traceId ?? "" })}>Eventos de esta traza</button> : null}
            {target ? <Link className="button button--small button--secondary" to={target}>Abrir {lowerFirst(entityLabel(entry.entityName))}</Link> : null}
            {entry.userId && canOpenActor ? <Link className="button button--small button--secondary" to={`/users/${entry.userId}`}>Abrir actor</Link> : null}
          </div>
        </div>
        <div className="dialog__actions"><button className="button" type="button" onClick={onClose}>Cerrar</button></div>
      </> : null}
    </dialog>
  );
}

/** "Cliente OAuth" → "cliente OAuth": the label inside a sentence keeps its acronyms. */
function lowerFirst(text: string): string {
  return text.charAt(0).toLowerCase() + text.slice(1);
}

function fileName(disposition: string | null): string | null {
  const match = disposition?.match(/filename="?([^";]+)"?/i);
  return match?.[1] ?? null;
}
