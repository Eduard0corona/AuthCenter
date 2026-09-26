import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { apiRequest, ApiError } from "../../api/client";
import type { EventDelivery, PagedResult } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { formatDate } from "../../utils/format";

interface ReplayRequest {
  delivery: EventDelivery;
  idempotencyKey: string;
}

export default function EventHooksPage() {
  const [deadOnly, setDeadOnly] = useState(false);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [target, setTarget] = useState<EventDelivery | null>(null);
  const [feedback, setFeedback] = useState("");
  const queryClient = useQueryClient();
  const deliveries = useQuery({
    queryKey: ["event-deliveries", deadOnly, page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => {
      const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
      if (deadOnly) query.set("status", "dead-letter");
      return apiRequest<PagedResult<EventDelivery>>(`/api/event-hooks/deliveries?${query.toString()}`, { signal });
    }
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
      await queryClient.invalidateQueries({ queryKey: ["event-deliveries"] });
    }
  });
  const selectFilter = (value: boolean) => { setDeadOnly(value); setPage(1); };

  return (
    <>
      <PageHeader eyebrow="Workflows" title="Entregas de Event Hooks" description="Diagnostica entregas y reintenta dead letters con confirmación explícita." actions={<div className="segmented"><button type="button" aria-pressed={!deadOnly} onClick={() => selectFilter(false)}>Todas</button><button type="button" aria-pressed={deadOnly} onClick={() => selectFilter(true)}>Dead letters</button></div>} />
      {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
      {replay.error ? <p className="alert alert--error" role="alert">{message(replay.error)}</p> : null}
      {deliveries.isPending ? <PageState title="Cargando entregas" busy /> : null}
      {deliveries.isError ? <PageState title="No pudimos cargar entregas" detail={message(deliveries.error)} tone="error" action={<button className="button" type="button" onClick={() => void deliveries.refetch()}>Reintentar</button>} /> : null}
      {deliveries.data?.items.length === 0 ? <PageState title="Sin entregas" detail={deadOnly ? "No existen dead letters pendientes." : "Todavía no hay eventos entregados."} /> : null}
      {deliveries.data && deliveries.data.items.length > 0 ? <div className="delivery-grid">{deliveries.data.items.map((delivery) => {
        const state = delivery.deliveredAt ? "Entregado" : delivery.deadLetteredAt ? "Dead letter" : "Pendiente";
        return <article className="delivery-card" key={delivery.id}><div className="delivery-card__heading"><StatusBadge active={state === "Entregado"} activeLabel={state} inactiveLabel={state} /><span>{delivery.hookName}</span></div><h2>{delivery.eventType}</h2><p className="mono">{delivery.eventId}</p><dl><div><dt>Intentos</dt><dd>{delivery.attemptCount}</dd></div><div><dt>Próximo intento</dt><dd>{formatDate(delivery.nextAttemptAt)}</dd></div></dl>{delivery.lastError ? <p className="delivery-error">{delivery.lastError}</p> : null}{delivery.deadLetteredAt ? <button className="button button--danger-quiet" type="button" onClick={() => setTarget(delivery)}>Reintentar entrega</button> : null}</article>;
      })}</div> : null}
      {deliveries.data && deliveries.data.totalCount > 0 ? <Pagination page={deliveries.data.page} pageSize={deliveries.data.pageSize} totalCount={deliveries.data.totalCount} totalPages={deliveries.data.totalPages} onPageChange={setPage} onPageSizeChange={(value) => { setPageSize(value); setPage(1); }} /> : null}
      <ConfirmDialog open={target !== null} title="Reintentar dead letter" detail={target ? `El evento ${target.eventId} del hook ${target.hookName} volverá a la cola con el contador reiniciado.` : ""} confirmLabel="Reintentar" busy={replay.isPending} onCancel={() => setTarget(null)} onConfirm={() => { if (target) replay.mutate({ delivery: target, idempotencyKey: crypto.randomUUID() }); }} />
    </>
  );
}

function message(error: unknown): string { return error instanceof ApiError ? error.message : "Ocurrió un error inesperado."; }
