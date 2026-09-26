import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { fetchAllPages } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { EventDelivery, EventDeliveryStatus, EventHook, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { DebouncedTextField } from "../../components/DebouncedTextField";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { useModalDialog } from "../../hooks/useModalDialog";
import { buildQuery, formatDate } from "../../utils/format";
import { dayBoundary } from "../system-log/entities";
import { formatPayload } from "./event-hook";

const STATUSES: Array<{ value: "" | EventDeliveryStatus; label: string }> = [
  { value: "", label: "Todas" },
  { value: "pending", label: "Pendientes" },
  { value: "delivered", label: "Entregadas" },
  { value: "dead-letter", label: "Dead letters" }
];

const STATUS_LABELS: Record<EventDeliveryStatus, string> = { pending: "Pendiente", delivered: "Entregada", "dead-letter": "Dead letter" };

interface ReplayRequest {
  delivery: EventDelivery;
  idempotencyKey: string;
}

export default function EventDeliveriesPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_EVENT_HOOKS_WRITE");
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const status = STATUSES.some((item) => item.value === params.get("status")) ? params.get("status") ?? "" : "";
  const hookId = params.get("hook") ?? "";
  const [target, setTarget] = useState<EventDelivery | null>(null);
  const [detailId, setDetailId] = useState<string | null>(null);
  const [feedback, setFeedback] = useState("");
  const queryClient = useQueryClient();
  const filterQuery = buildQuery({
    status: status || null,
    hookId: hookId || null,
    eventType: params.get("eventType"),
    eventId: params.get("eventId"),
    fromUtc: dayBoundary(params.get("from"), "start"),
    toUtc: dayBoundary(params.get("to"), "end")
  });
  const hooks = useQuery({
    queryKey: ["event-hooks", "catalog"],
    queryFn: ({ signal }) => fetchAllPages<EventHook>("/api/event-hooks", signal)
  });
  const deliveries = useQuery({
    queryKey: ["event-deliveries", filterQuery, page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => apiRequest<PagedResult<EventDelivery>>(`/api/event-hooks/deliveries?${filterQuery}${filterQuery ? "&" : ""}${buildQuery({ page, pageSize })}`, { signal })
  });
  // The key is created when the operator confirms, so an automatic retry of the same request is
  // idempotent while a later, deliberate replay of a delivery that failed again is not.
  const replay = useMutation({
    mutationFn: ({ delivery, idempotencyKey }: ReplayRequest) => apiRequest<void>(`/api/event-hooks/deliveries/${delivery.id}/replay`, {
      method: "POST",
      headers: { "Idempotency-Key": idempotencyKey }
    }),
    onSuccess: async (_, { delivery }) => {
      setFeedback(`El evento ${delivery.eventId} volvió a la cola.`);
      setTarget(null);
      setDetailId(null);
      await queryClient.invalidateQueries({ queryKey: ["event-deliveries"] });
      await queryClient.invalidateQueries({ queryKey: ["event-delivery", delivery.id] });
    }
  });
  const update = useCallback((values: Record<string, string>, replace = true) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [name, value] of Object.entries(values)) {
      if (value) next.set(name, value); else next.delete(name);
    }
    if (!("page" in values)) next.delete("page");
    return next;
  }, { replace }), [setParams]);
  const commitEventType = useCallback((value: string) => update({ eventType: value }), [update]);
  const commitEventId = useCallback((value: string) => update({ eventId: value }), [update]);
  const filtered = Boolean(filterQuery);

  return (
    <>
      <Breadcrumbs items={[{ label: "Event Hooks", to: "/event-hooks" }, { label: "Entregas" }]} />
      <PageHeader
        eyebrow="Operación"
        title="Entregas de Event Hooks"
        description="Diagnostica entregas, revisa el payload firmado y reintenta dead letters con confirmación explícita."
        actions={<div className="segmented" role="group" aria-label="Estado de la entrega">{STATUSES.map((item) => <button key={item.value || "all"} type="button" aria-pressed={status === item.value} onClick={() => update({ status: item.value }, false)}>{item.label}</button>)}</div>}
      />
      <section className="toolbar toolbar--wide" aria-label="Filtros de entregas">
        <label className="field"><span>Hook</span><select value={hookId} onChange={(event) => update({ hook: event.target.value }, false)}><option value="">Todos los hooks</option>{hooks.data?.map((hook) => <option key={hook.id} value={hook.id}>{hook.name}</option>)}</select></label>
        <DebouncedTextField label="Tipo de evento" value={params.get("eventType") ?? ""} onCommit={commitEventType} placeholder="USER_CREATED" normalize={(value) => value.trim().toUpperCase()} />
        <DebouncedTextField label="ID del evento" value={params.get("eventId") ?? ""} onCommit={commitEventId} />
        <label className="field"><span>Desde (hora local)</span><input type="date" value={params.get("from") ?? ""} onChange={(event) => update({ from: event.target.value }, false)} /></label>
        <label className="field"><span>Hasta (hora local)</span><input type="date" value={params.get("to") ?? ""} onChange={(event) => update({ to: event.target.value }, false)} /></label>
      </section>
      {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
      {replay.error ? <p className="alert alert--error" role="alert">{errorMessage(replay.error)}</p> : null}
      {deliveries.isPending ? <PageState title="Cargando entregas" busy /> : null}
      {deliveries.isError ? <PageState title="No pudimos cargar entregas" detail={errorMessage(deliveries.error)} tone="error" action={<button className="button" type="button" onClick={() => void deliveries.refetch()}>Reintentar</button>} /> : null}
      {deliveries.data?.items.length === 0 ? <PageState title="Sin entregas" detail={status === "dead-letter" ? "No existen dead letters pendientes." : filtered ? "No encontramos entregas con estos filtros." : "Todavía no hay eventos entregados."} /> : null}
      {deliveries.data && deliveries.data.items.length > 0 ? <>
        <div className="data-table" tabIndex={0} role="region" aria-label="Entregas de Event Hooks, desplazamiento horizontal" aria-busy={deliveries.isFetching || undefined}>
          <table>
            <caption className="sr-only">Entregas de Event Hooks</caption>
            <thead><tr><th scope="col">Evento</th><th scope="col">Hook</th><th scope="col">Estado</th><th scope="col">Intentos</th><th scope="col">Registrada</th><th scope="col"><span className="sr-only">Acciones</span></th></tr></thead>
            <tbody>{deliveries.data.items.map((delivery) => {
              const state = deliveryStatus(delivery);
              return <tr key={delivery.id}>
                <td><code>{delivery.eventType}</code><span className="cell-detail mono">{delivery.eventId}</span>{delivery.lastError ? <span className="cell-detail delivery-error-inline">{delivery.lastError}</span> : null}</td>
                <td><Link to={`/event-hooks/${delivery.hookId}`}>{delivery.hookName}</Link></td>
                <td><span className={`tag delivery-status--${state}`}>{STATUS_LABELS[state]}</span><span className="cell-detail">{state === "delivered" ? `Entregada ${formatDate(delivery.deliveredAt)}` : state === "dead-letter" ? `Desde ${formatDate(delivery.deadLetteredAt)}` : `Próximo intento ${formatDate(delivery.nextAttemptAt)}`}</span></td>
                <td>{delivery.attemptCount}</td>
                <td>{formatDate(delivery.createdAt ?? null)}</td>
                <td className="table-action"><div className="button-row button-row--end"><button className="button button--small button--secondary" type="button" onClick={() => setDetailId(delivery.id)}>Detalle<span className="sr-only"> de {delivery.eventType} {delivery.eventId}</span></button>{canWrite && state === "dead-letter" ? <button className="button button--small button--danger-quiet" type="button" onClick={() => setTarget(delivery)}>Reintentar entrega<span className="sr-only"> {delivery.eventId}</span></button> : null}</div></td>
              </tr>;
            })}</tbody>
          </table>
        </div>
        <Pagination page={deliveries.data.page} pageSize={deliveries.data.pageSize} totalCount={deliveries.data.totalCount} totalPages={deliveries.data.totalPages} onPageChange={(value) => update({ page: String(value) }, false)} onPageSizeChange={(value) => update({ pageSize: String(value) }, false)} />
      </> : null}
      <DeliveryDetailDialog deliveryId={detailId} canReplay={canWrite} onClose={() => setDetailId(null)} onReplay={(delivery) => setTarget(delivery)} />
      <ConfirmDialog open={target !== null} title="Reintentar dead letter" detail={target ? `El evento ${target.eventId} del hook ${target.hookName} volverá a la cola con el contador reiniciado.` : ""} confirmLabel="Reintentar" busy={replay.isPending} onCancel={() => setTarget(null)} onConfirm={() => { if (target) replay.mutate({ delivery: target, idempotencyKey: crypto.randomUUID() }); }} />
    </>
  );
}

function deliveryStatus(delivery: EventDelivery): EventDeliveryStatus {
  return delivery.status ?? (delivery.deliveredAt ? "delivered" : delivery.deadLetteredAt ? "dead-letter" : "pending");
}

function DeliveryDetailDialog({ deliveryId, canReplay, onClose, onReplay }: { deliveryId: string | null; canReplay: boolean; onClose: () => void; onReplay: (delivery: EventDelivery) => void }) {
  const dialog = useModalDialog(deliveryId !== null);
  const detail = useQuery({
    queryKey: ["event-delivery", deliveryId],
    enabled: deliveryId !== null,
    queryFn: ({ signal }) => apiRequest<EventDelivery>(`/api/event-hooks/deliveries/${deliveryId}`, { signal })
  });
  const delivery = detail.data;
  const state = delivery ? deliveryStatus(delivery) : null;
  return (
    <dialog ref={dialog} className="dialog dialog--wide" aria-labelledby="delivery-detail-title" onCancel={(event) => { event.preventDefault(); onClose(); }}>
      <div className="dialog__content">
        <p className="eyebrow">Entrega de Event Hook</p>
        <h2 id="delivery-detail-title">{delivery ? <code>{delivery.eventType}</code> : "Detalle de la entrega"}</h2>
        {detail.isPending ? <p className="muted">Cargando la entrega…</p> : null}
        {detail.isError ? <p className="alert alert--error" role="alert">{errorMessage(detail.error)}</p> : null}
        {delivery && state ? <>
          <dl className="detail-list">
            <div><dt>Estado</dt><dd><span className={`tag delivery-status--${state}`}>{STATUS_LABELS[state]}</span></dd></div>
            <div><dt>Hook</dt><dd>{delivery.hookName}</dd></div>
            <div><dt>ID del evento</dt><dd className="mono">{delivery.eventId}</dd></div>
            <div><dt>Intentos</dt><dd>{delivery.attemptCount}</dd></div>
            <div><dt>Registrada</dt><dd>{formatDate(delivery.createdAt ?? null)}</dd></div>
            <div><dt>{state === "delivered" ? "Entregada" : state === "dead-letter" ? "Dead letter desde" : "Próximo intento"}</dt><dd>{formatDate(state === "delivered" ? delivery.deliveredAt : state === "dead-letter" ? delivery.deadLetteredAt : delivery.nextAttemptAt)}</dd></div>
          </dl>
          {delivery.lastError ? <><h3>Último error</h3><pre className="mono code-block delivery-error">{delivery.lastError}</pre></> : null}
          <h3>Payload firmado</h3>
          {delivery.payload ? <pre className="mono code-block">{formatPayload(delivery.payload)}</pre> : <p className="muted">La entrega no conserva el payload.</p>}
        </> : null}
      </div>
      <div className="dialog__actions">
        {delivery && canReplay && state === "dead-letter" ? <button className="button button--danger-quiet" type="button" onClick={() => onReplay(delivery)}>Reintentar entrega</button> : null}
        <button className="button" type="button" onClick={onClose}>Cerrar</button>
      </div>
    </dialog>
  );
}
