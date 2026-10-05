import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useCallback } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { EventHook, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { DebouncedTextField } from "../../components/DebouncedTextField";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { buildQuery, formatDate } from "../../utils/format";
import { describeSubscription } from "./event-hook";

export default function EventHooksPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_EVENT_HOOKS_WRITE");
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const search = params.get("search") ?? "";
  const active = params.get("active") ?? "";
  const verified = params.get("verified") ?? "";
  const hooks = useQuery({
    queryKey: ["event-hooks", search, active, verified, page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => apiRequest<PagedResult<EventHook>>(`/api/event-hooks?${buildQuery({ page, pageSize, search, isActive: active || null, isVerified: verified || null })}`, { signal })
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
        eyebrow="Operación"
        title="Webhooks de eventos"
        description="Envía eventos de seguridad y administración a tus sistemas por HTTPS, firmados con HMAC-SHA256."
        actions={<><Link className="button button--secondary" to="/event-hooks/deliveries">Ver entregas</Link>{canWrite ? <Link className="button" to="/event-hooks/new">Nuevo webhook</Link> : null}</>}
      />
      <section className="toolbar toolbar--wide" aria-label="Filtros de webhooks">
        <DebouncedTextField label="Buscar" value={search} onCommit={commitSearch} placeholder="Nombre o URL" />
        <label className="field"><span>Estado</span><select value={active} onChange={(event) => update("active", event.target.value, false)}><option value="">Todos</option><option value="true">Activos</option><option value="false">Desactivados</option></select></label>
        <label className="field"><span>Verificación</span><select value={verified} onChange={(event) => update("verified", event.target.value, false)}><option value="">Todas</option><option value="true">Verificados</option><option value="false">Sin verificar</option></select></label>
      </section>
      {hooks.isPending ? <PageState title="Cargando webhooks" busy /> : null}
      {hooks.isError ? <PageState title="No pudimos cargar los webhooks" detail={errorMessage(hooks.error)} tone="error" action={<button className="button" type="button" onClick={() => void hooks.refetch()}>Reintentar</button>} /> : null}
      {hooks.data && hooks.data.items.length === 0 ? <PageState title="Sin webhooks" detail={search || active || verified ? "No encontramos webhooks con estos filtros." : "Crea un webhook para recibir eventos en tu SIEM, CRM o flujo de automatización."} action={canWrite && !search && !active && !verified ? <Link className="button" to="/event-hooks/new">Crear el primer webhook</Link> : undefined} /> : null}
      {hooks.data && hooks.data.items.length > 0 ? <>
        <div className="data-table" tabIndex={0} role="region" aria-label="Webhooks de eventos, desplazamiento horizontal" aria-busy={hooks.isFetching || undefined}>
          <table>
            <caption className="sr-only">Webhooks de eventos</caption>
            <thead><tr><th scope="col">Webhook</th><th scope="col">Alcance</th><th scope="col">Eventos</th><th scope="col">Estado</th><th scope="col"><span className="sr-only">Acciones</span></th></tr></thead>
            <tbody>{hooks.data.items.map((hook) => <tr key={hook.id}>
              <td><strong>{hook.name}</strong><span className="cell-detail mono">{hook.url}</span></td>
              <td>{hook.applicationName ?? "Toda la plataforma"}</td>
              <td>{describeSubscription(hook.eventTypes)}</td>
              <td><StatusBadge active={hook.isActive} activeLabel="Activo" inactiveLabel="Desactivado" /> {hook.isVerified ? <span className="tag tag--direct">Verificado {formatDate(hook.verifiedAt)}</span> : <span className="tag tag--warning">Sin verificar</span>}</td>
              <td className="table-action"><Link className="button button--small button--secondary" to={`/event-hooks/${hook.id}`}>Abrir<span className="sr-only"> {hook.name}</span></Link></td>
            </tr>)}</tbody>
          </table>
        </div>
        <Pagination page={hooks.data.page} pageSize={hooks.data.pageSize} totalCount={hooks.data.totalCount} totalPages={hooks.data.totalPages} onPageChange={(value) => update("page", String(value), false)} onPageSizeChange={(value) => update("pageSize", String(value), false)} />
      </> : null}
    </>
  );
}
