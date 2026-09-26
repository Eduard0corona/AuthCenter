import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useCallback, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useApplicationsCatalog } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { AccessReview, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { buildQuery, formatDate } from "../../utils/format";
import { reviewStatusLabels } from "./governance";

export default function AccessReviewsPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_GOVERNANCE_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const status = params.get("status") ?? "";
  const applicationSystemId = params.get("application") ?? "";
  const applications = useApplicationsCatalog(canReadApplications);
  const reviews = useQuery({
    queryKey: ["access-reviews", status, applicationSystemId, page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => apiRequest<PagedResult<AccessReview>>(`/api/governance/access-reviews?${buildQuery({ page, pageSize, status, applicationSystemId })}`, { signal })
  });
  const update = useCallback((name: string, value: string) => setParams((current) => {
    const next = new URLSearchParams(current);
    if (value) next.set(name, value); else next.delete(name);
    if (name !== "page") next.delete("page");
    return next;
  }), [setParams]);
  // Read once: rendering stays pure (react-hooks purity).
  const [now] = useState(() => Date.now());

  return <>
    <PageHeader
      eyebrow="Gobierno"
      title="Revisiones de acceso"
      description="Campañas periódicas en las que los responsables de cada aplicación confirman quién conserva el acceso. Lo que nadie revise al vencer se mantiene o se revoca, según la campaña."
      actions={canWrite && canReadApplications ? <Link className="button" to="/access-reviews/new">Nueva revisión</Link> : undefined}
    />
    <section className="toolbar" aria-label="Filtros de revisiones">
      <label className="field"><span>Estado</span><select value={status} onChange={(event) => update("status", event.target.value)}><option value="">Todas</option>{Object.entries(reviewStatusLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
      {canReadApplications ? <label className="field"><span>Aplicación</span><select value={applicationSystemId} onChange={(event) => update("application", event.target.value)}><option value="">Todas</option>{applications.data?.map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></label> : null}
    </section>
    {reviews.isPending ? <PageState title="Cargando revisiones" busy /> : null}
    {reviews.isError ? <PageState title="No pudimos cargar las revisiones" detail={errorMessage(reviews.error)} tone="error" action={<button className="button" type="button" onClick={() => void reviews.refetch()}>Reintentar</button>} /> : null}
    {reviews.data && reviews.data.items.length === 0 ? <PageState title="Sin revisiones" detail={status || applicationSystemId ? "No encontramos revisiones con estos filtros." : "Crea una revisión para que los responsables confirmen quién debe conservar el acceso a una aplicación."} /> : null}
    {reviews.data && reviews.data.items.length > 0 ? <>
      <div className="data-table" tabIndex={0} role="region" aria-label="Revisiones de acceso, desplazamiento horizontal" aria-busy={reviews.isFetching || undefined}>
        <table>
          <caption className="sr-only">Revisiones de acceso</caption>
          <thead><tr><th scope="col">Revisión</th><th scope="col">Avance</th><th scope="col">Vence</th><th scope="col">Estado</th><th scope="col"><span className="sr-only">Acciones</span></th></tr></thead>
          <tbody>{reviews.data.items.map((review) => {
            const overdue = review.status === "Active" && new Date(review.dueAt).getTime() < now;
            return <tr key={review.id}>
              <td><strong>{review.name}</strong><span className="cell-detail">{review.applicationName}{review.recurrenceMonths ? ` · se repite cada ${review.recurrenceMonths} ${review.recurrenceMonths === 1 ? "mes" : "meses"}` : ""}</span></td>
              <td>{review.totalItems - review.pendingItems} de {review.totalItems} revisados<span className="cell-detail">{review.keptItems} se mantienen · {review.revokedItems} revocados{review.remediationItems ? ` · ${review.remediationItems} por quitar de grupos` : ""}</span></td>
              <td>{formatDate(review.dueAt)}{overdue ? <span className="cell-detail">Vencida: se cerrará en breve</span> : null}</td>
              <td><span className={`tag ${review.status === "Active" ? "tag--warning" : review.status === "Cancelled" ? "tag--inactive" : ""}`}>{reviewStatusLabels[review.status]}</span></td>
              <td className="table-action"><Link className="button button--small button--secondary" to={`/access-reviews/${review.id}`}>{review.status === "Active" && canWrite ? "Revisar" : "Consultar"}<span className="sr-only"> {review.name}</span></Link></td>
            </tr>;
          })}</tbody>
        </table>
      </div>
      <Pagination page={reviews.data.page} pageSize={reviews.data.pageSize} totalCount={reviews.data.totalCount} totalPages={reviews.data.totalPages} onPageChange={(value) => update("page", String(value))} onPageSizeChange={(value) => update("pageSize", String(value))} />
    </> : null}
  </>;
}
