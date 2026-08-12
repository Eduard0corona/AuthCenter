import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { apiRequest, ApiError } from "../../api/client";
import type { EventDelivery } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { StatusBadge } from "../../components/StatusBadge";
import { formatDate } from "../../utils/format";

export default function EventHooksPage() {
  const [deadOnly, setDeadOnly] = useState(false);
  const [target, setTarget] = useState<EventDelivery | null>(null);
  const [feedback, setFeedback] = useState("");
  const queryClient = useQueryClient();
  const deliveries = useQuery({
    queryKey: ["event-deliveries", deadOnly],
    queryFn: ({ signal }) => apiRequest<EventDelivery[]>(`/api/event-hooks/deliveries?deadLettersOnly=${deadOnly}`, { signal })
  });
  const replay = useMutation({
    mutationFn: (delivery: EventDelivery) => apiRequest<void>(`/api/event-hooks/deliveries/${delivery.id}/replay`, { method: "POST" }),
    onSuccess: async (_, delivery) => {
      setFeedback(`El evento ${delivery.eventId} volvió a la cola.`);
      setTarget(null);
      await queryClient.invalidateQueries({ queryKey: ["event-deliveries"] });
    }
  });

  return (
    <>
      <PageHeader eyebrow="Workflows" title="Entregas de Event Hooks" description="Diagnostica entregas y reintenta dead letters con confirmación explícita." actions={<div className="segmented"><button type="button" aria-pressed={!deadOnly} onClick={() => setDeadOnly(false)}>Todas</button><button type="button" aria-pressed={deadOnly} onClick={() => setDeadOnly(true)}>Dead letters</button></div>} />
      <p className="alert alert--info">La API actual limita esta vista a 200 entregas. La paginación server-side permanece como dependencia de backend.</p>
      {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
      {replay.error ? <p className="alert alert--error" role="alert">{message(replay.error)}</p> : null}
      {deliveries.isPending ? <PageState title="Cargando entregas" busy /> : null}
      {deliveries.isError ? <PageState title="No pudimos cargar entregas" detail={message(deliveries.error)} tone="error" action={<button className="button" type="button" onClick={() => void deliveries.refetch()}>Reintentar</button>} /> : null}
      {deliveries.data?.length === 0 ? <PageState title="Sin entregas" detail={deadOnly ? "No existen dead letters pendientes." : "Todavía no hay eventos entregados."} /> : null}
      {deliveries.data && deliveries.data.length > 0 ? <div className="delivery-grid">{deliveries.data.map((delivery) => {
        const state = delivery.deliveredAt ? "Entregado" : delivery.deadLetteredAt ? "Dead letter" : "Pendiente";
        return <article className="delivery-card" key={delivery.id}><div className="delivery-card__heading"><StatusBadge active={state === "Entregado"} activeLabel={state} inactiveLabel={state} /><span>{delivery.hookName}</span></div><h2>{delivery.eventType}</h2><p className="mono">{delivery.eventId}</p><dl><div><dt>Intentos</dt><dd>{delivery.attemptCount}</dd></div><div><dt>Próximo intento</dt><dd>{formatDate(delivery.nextAttemptAt)}</dd></div></dl>{delivery.lastError ? <p className="delivery-error">{delivery.lastError}</p> : null}{delivery.deadLetteredAt ? <button className="button button--danger-quiet" type="button" onClick={() => setTarget(delivery)}>Reintentar entrega</button> : null}</article>;
      })}</div> : null}
      <ConfirmDialog open={target !== null} title="Reintentar dead letter" detail={target ? `El evento ${target.eventId} del hook ${target.hookName} volverá a la cola con el contador reiniciado.` : ""} confirmLabel="Reintentar" busy={replay.isPending} onCancel={() => setTarget(null)} onConfirm={() => { if (target) replay.mutate(target); }} />
    </>
  );
}

function message(error: unknown): string { return error instanceof ApiError ? error.message : "Ocurrió un error inesperado."; }
