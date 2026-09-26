import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { AccessReview, AccessReviewItem, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { DebouncedTextField } from "../../components/DebouncedTextField";
import { DecisionDialog } from "../../components/DecisionDialog";
import { HistoryLink } from "../../components/HistoryLink";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { buildQuery, formatDate } from "../../utils/format";
import { decisionLabels, governanceMessages, reviewStatusLabels } from "./governance";

interface Decision {
  item: AccessReviewItem;
  keep: boolean;
}

export default function AccessReviewDetailPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_GOVERNANCE_WRITE");
  const { reviewId = "" } = useParams();
  const queryClient = useQueryClient();
  const [filter, setFilter] = useState("Pending");
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [decision, setDecision] = useState<Decision | null>(null);
  const [confirmCancel, setConfirmCancel] = useState(false);
  const [feedback, setFeedback] = useState("");
  const review = useQuery({
    queryKey: ["access-review", reviewId],
    queryFn: ({ signal }) => apiRequest<AccessReview>(`/api/governance/access-reviews/${reviewId}`, { signal })
  });
  const items = useQuery({
    queryKey: ["access-review-items", reviewId, filter, search, page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => apiRequest<PagedResult<AccessReviewItem>>(`/api/governance/access-reviews/${reviewId}/items?${buildQuery({
      page, pageSize, search,
      decision: filter === "Pending" || filter === "Keep" || filter === "Revoke" ? filter : null,
      remediationRequired: filter === "remediation" ? true : null
    })}`, { signal })
  });
  const decide = useMutation({
    mutationFn: ({ item, keep, comment }: Decision & { comment: string }) =>
      apiRequest<AccessReviewItem>(`/api/governance/access-reviews/${reviewId}/items/${item.id}/decision`, { method: "POST", body: JSON.stringify({ decision: keep ? "Keep" : "Revoke", comment: comment || null }) }),
    onSuccess: async (item) => {
      setDecision(null);
      setFeedback(`${item.user.fullName}: ${item.decision === "Keep" ? "el acceso se mantiene" : item.remediationRequired ? "acceso directo revocado; sus grupos todavía le dan acceso" : "acceso revocado"}.`);
      await refreshReview();
    }
  });
  const cancel = useMutation({
    mutationFn: () => apiRequest<AccessReview>(`/api/governance/access-reviews/${reviewId}/cancel`, { method: "POST" }),
    onSuccess: async () => {
      setConfirmCancel(false);
      setFeedback("La revisión se canceló. Las decisiones ya tomadas se conservan.");
      await refreshReview();
    }
  });

  async function refreshReview(): Promise<void> {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["access-review", reviewId] }),
      queryClient.invalidateQueries({ queryKey: ["access-review-items", reviewId] }),
      queryClient.invalidateQueries({ queryKey: ["access-reviews"] }),
      queryClient.invalidateQueries({ queryKey: ["admin-dashboard"] })
    ]);
  }

  if (review.isPending) return <PageState title="Cargando revisión" busy />;
  if (review.isError) return <PageState title="No pudimos cargar la revisión" detail={errorMessage(review.error)} tone="error" action={<Link className="button" to="/access-reviews">Volver</Link>} />;
  const current = review.data;
  const active = current.status === "Active";

  return <>
    <Breadcrumbs items={[{ label: "Revisiones de acceso", to: "/access-reviews" }, { label: current.name }]} />
    <PageHeader eyebrow={current.applicationName} title={current.name} description={active ? `Decide quién conserva el acceso antes del ${formatDate(current.dueAt)}. Lo que nadie revise se ${current.revokeUnreviewed ? "revocará" : "mantendrá"}.` : `Revisión ${reviewStatusLabels[current.status].toLowerCase()} el ${formatDate(current.completedAt)}.`} actions={<><HistoryLink entityName="AccessReviewCampaign" entityId={current.id} /><Link className="button button--secondary" to="/access-reviews">Volver al listado</Link></>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    <section className="settings-panel" aria-labelledby="review-summary">
      <div className="settings-panel__heading"><div><h2 id="review-summary">Resumen</h2><p>Los responsables deciden desde su portal; revocar quita el acceso directo al momento. El acceso que dan los grupos lo quita un administrador del grupo.</p></div><span className={`tag ${active ? "tag--warning" : current.status === "Cancelled" ? "tag--inactive" : ""}`}>{reviewStatusLabels[current.status]}</span></div>
      <dl className="profile-summary">
        <div><dt>Revisados</dt><dd>{current.totalItems - current.pendingItems} de {current.totalItems}</dd></div>
        <div><dt>Se mantienen</dt><dd>{current.keptItems}</dd></div>
        <div><dt>Revocados</dt><dd>{current.revokedItems}</dd></div>
        <div><dt>Por quitar de grupos</dt><dd>{current.remediationItems}</dd></div>
        <div><dt>Iniciada</dt><dd>{formatDate(current.createdAt)}</dd></div>
        <div><dt>Fecha límite</dt><dd>{formatDate(current.dueAt)}</dd></div>
        <div><dt>Repetición</dt><dd>{current.recurrenceMonths ? `Cada ${current.recurrenceMonths} ${current.recurrenceMonths === 1 ? "mes" : "meses"}` : "No se repite"}</dd></div>
        <div><dt>Responsables</dt><dd>{current.reviewers.length ? current.reviewers.map((reviewer) => reviewer.fullName).join(", ") : "Sin responsables: decide un administrador"}</dd></div>
      </dl>
      {current.previousCampaignId ? <p><Link to={`/access-reviews/${current.previousCampaignId}`}>Ver la campaña anterior</Link></p> : null}
    </section>
    <section className="settings-panel" aria-labelledby="review-items">
      <div className="settings-panel__heading"><div><h2 id="review-items">Accesos</h2><p>Nadie revisa su propio acceso.</p></div></div>
      <div className="toolbar" role="group" aria-label="Filtros de accesos">
        <label className="field"><span>Mostrar</span><select value={filter} onChange={(event) => { setFilter(event.target.value); setPage(1); }}>
          <option value="Pending">Sin revisar</option>
          <option value="all">Todos</option>
          <option value="Keep">Se mantienen</option>
          <option value="Revoke">Revocados</option>
          <option value="remediation">Por quitar de grupos</option>
        </select></label>
        <DebouncedTextField label="Buscar" value={search} onCommit={(value) => { setSearch(value); setPage(1); }} placeholder="Nombre o correo" />
      </div>
      {items.isPending ? <PageState title="Cargando accesos" busy /> : null}
      {items.isError ? <PageState title="No pudimos cargar los accesos" detail={errorMessage(items.error, governanceMessages)} tone="error" action={<button className="button" type="button" onClick={() => void items.refetch()}>Reintentar</button>} /> : null}
      {items.data && items.data.items.length === 0 ? <PageState title={filter === "Pending" ? "No queda nada por revisar" : "Sin accesos"} detail={filter === "Pending" ? "Todos los accesos de esta revisión tienen una decisión." : "No hay accesos con estos filtros."} /> : null}
      {items.data && items.data.items.length > 0 ? <>
        <div className="data-table" tabIndex={0} role="region" aria-label="Accesos de la revisión, desplazamiento horizontal" aria-busy={items.isFetching || undefined}>
          <table>
            <caption className="sr-only">Accesos de la revisión</caption>
            <thead><tr><th scope="col">Usuario</th><th scope="col">Acceso</th><th scope="col">Decisión</th><th scope="col"><span className="sr-only">Acciones</span></th></tr></thead>
            <tbody>{items.data.items.map((item) => <tr key={item.id}>
              <td><Link to={`/users/${item.user.id}`}><strong>{item.user.fullName}</strong></Link><span className="cell-detail">{item.user.email}</span></td>
              <td>{item.hasDirectAccess ? <span className="tag tag--direct">Directo</span> : null}{item.groups.map((group) => <span className="tag tag--inherited" key={group}>{group}</span>)}{item.roles.length ? <span className="cell-detail">Roles: {item.roles.join(", ")}</span> : null}</td>
              <td>{decisionLabels[item.decision]}{item.decidedAutomatically ? <span className="cell-detail">Por fecha límite</span> : item.decidedBy ? <span className="cell-detail">{item.decidedBy.fullName} · {formatDate(item.decidedAt)}</span> : null}
                {item.remediationRequired ? <span className="cell-detail">{item.outcome}</span> : null}
                {item.comment ? <span className="cell-detail">“{item.comment}”</span> : null}</td>
              <td className="table-action">{canWrite && item.canDecide ? <div className="button-group">
                <button className="button button--small button--secondary" type="button" onClick={() => { decide.reset(); setDecision({ item, keep: true }); }}>Mantener<span className="sr-only"> el acceso de {item.user.fullName}</span></button>
                <button className="button button--small button--danger-quiet" type="button" onClick={() => { decide.reset(); setDecision({ item, keep: false }); }}>Revocar<span className="sr-only"> el acceso de {item.user.fullName}</span></button>
              </div> : null}</td>
            </tr>)}</tbody>
          </table>
        </div>
        <Pagination page={items.data.page} pageSize={items.data.pageSize} totalCount={items.data.totalCount} totalPages={items.data.totalPages} onPageChange={setPage} onPageSizeChange={(value) => { setPageSize(value); setPage(1); }} />
      </> : null}
    </section>
    {active && canWrite ? <section className="settings-panel settings-panel--actions" aria-labelledby="review-cancel">
      <div className="settings-panel__heading"><div><h2 id="review-cancel">Cancelar la revisión</h2><p>Las decisiones tomadas se conservan; lo que falte no se decide y la revisión no se repite.</p></div></div>
      {cancel.error ? <p className="alert alert--error" role="alert">{errorMessage(cancel.error, governanceMessages)}</p> : null}
      <div className="button-group"><button className="button button--danger-quiet" type="button" onClick={() => { cancel.reset(); setConfirmCancel(true); }}>Cancelar revisión</button></div>
    </section> : null}
    <DecisionDialog
      key={decision ? `${decision.item.id}-${decision.keep}` : "none"}
      open={decision !== null}
      title={decision?.keep ? "Mantener el acceso" : "Revocar el acceso"}
      detail={decision ? decision.keep ? `${decision.item.user.fullName} conservará el acceso a ${current.applicationName}.` : `${decision.item.user.fullName} perderá el acceso directo a ${current.applicationName}${decision.item.groups.length ? `; el que le dan los grupos ${decision.item.groups.join(", ")} debe quitarse en cada grupo` : ""}.` : ""}
      confirmLabel={decision?.keep ? "Mantener" : "Revocar"}
      commentLabel="Comentario (opcional)"
      dangerous={decision?.keep === false}
      busy={decide.isPending}
      error={decide.error ? errorMessage(decide.error, governanceMessages) : null}
      onCancel={() => setDecision(null)}
      onConfirm={(comment) => { if (decision) decide.mutate({ ...decision, comment }); }}
    />
    <ConfirmDialog open={confirmCancel} title="Cancelar la revisión" detail={`La revisión ${current.name} dejará de estar en curso. Esta acción queda auditada y no se puede deshacer.`} confirmLabel="Cancelar revisión" dangerous busy={cancel.isPending} error={cancel.error} onCancel={() => setConfirmCancel(false)} onConfirm={() => cancel.mutate()} />
  </>;
}
