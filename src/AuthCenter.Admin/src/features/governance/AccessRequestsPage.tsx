import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useApplicationsCatalog } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { AccessRequest, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { DebouncedTextField } from "../../components/DebouncedTextField";
import { DecisionDialog } from "../../components/DecisionDialog";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { buildQuery, formatDate } from "../../utils/format";
import { governanceMessages, requestSourceLabels, requestStatusLabels } from "./governance";

interface Decision {
  request: AccessRequest;
  approve: boolean;
}

export default function AccessRequestsPage() {
  const { permissions, user } = useSession();
  const canWrite = permissions.has("AUTHCENTER_GOVERNANCE_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const status = params.get("status") ?? "Pending";
  const applicationSystemId = params.get("application") ?? "";
  const search = params.get("search") ?? "";
  const [decision, setDecision] = useState<Decision | null>(null);
  const [feedback, setFeedback] = useState("");
  const applications = useApplicationsCatalog(canReadApplications);
  const requests = useQuery({
    queryKey: ["access-requests", status, applicationSystemId, search, page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => apiRequest<PagedResult<AccessRequest>>(`/api/governance/access-requests?${buildQuery({ page, pageSize, status: status === "all" ? null : status, applicationSystemId, search })}`, { signal })
  });
  const decide = useMutation({
    mutationFn: ({ request, approve, comment }: Decision & { comment: string }) =>
      apiRequest<AccessRequest>(`/api/governance/access-requests/${request.id}/${approve ? "approve" : "reject"}`, { method: "POST", body: JSON.stringify({ comment: comment || null }) }),
    onSuccess: async (result) => {
      setDecision(null);
      setFeedback(`${result.requester.fullName}: solicitud ${requestStatusLabels[result.status].toLowerCase()}. Le avisamos por correo.`);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["access-requests"] }),
        queryClient.invalidateQueries({ queryKey: ["admin-dashboard"] })
      ]);
    }
  });
  const update = useCallback((name: string, value: string, replace = true) => setParams((current) => {
    const next = new URLSearchParams(current);
    if (value) next.set(name, value); else next.delete(name);
    if (name !== "page") next.delete("page");
    return next;
  }, { replace }), [setParams]);
  const commitSearch = useCallback((value: string) => update("search", value), [update]);

  return <>
    <PageHeader eyebrow="Gobierno" title="Solicitudes de acceso" description="Solicitudes del portal, registros que requieren aprobación y altas pendientes. Los responsables de cada aplicación las deciden desde su portal; aquí puedes decidir cualquiera, excepto las tuyas." />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    <section className="toolbar" aria-label="Filtros de solicitudes">
      <label className="field"><span>Estado</span><select value={status} onChange={(event) => update("status", event.target.value, false)}>
        <option value="all">Todas</option>
        {Object.entries(requestStatusLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
      </select></label>
      {canReadApplications ? <label className="field"><span>Aplicación</span><select value={applicationSystemId} onChange={(event) => update("application", event.target.value, false)}><option value="">Todas</option>{applications.data?.map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></label> : null}
      <DebouncedTextField label="Buscar" value={search} onCommit={commitSearch} placeholder="Nombre o correo de quien pide" />
    </section>
    {requests.isPending ? <PageState title="Cargando solicitudes" busy /> : null}
    {requests.isError ? <PageState title="No pudimos cargar las solicitudes" detail={errorMessage(requests.error)} tone="error" action={<button className="button" type="button" onClick={() => void requests.refetch()}>Reintentar</button>} /> : null}
    {requests.data && requests.data.items.length === 0 ? <PageState title="Sin solicitudes" detail={status === "Pending" ? "No hay solicitudes esperando una decisión." : "No encontramos solicitudes con estos filtros."} /> : null}
    {requests.data && requests.data.items.length > 0 ? <>
      <div className="data-table" tabIndex={0} role="region" aria-label="Solicitudes de acceso, desplazamiento horizontal" aria-busy={requests.isFetching || undefined}>
        <table>
          <caption className="sr-only">Solicitudes de acceso</caption>
          <thead><tr><th scope="col">Quién</th><th scope="col">Qué pide</th><th scope="col">Justificación</th><th scope="col">Estado</th><th scope="col"><span className="sr-only">Acciones</span></th></tr></thead>
          <tbody>{requests.data.items.map((request) => <tr key={request.id}>
            <td><Link to={`/users/${request.requester.id}`}><strong>{request.requester.fullName}</strong></Link><span className="cell-detail">{request.requester.email}</span></td>
            <td>{request.applicationName}{request.roleName ? <span className="cell-detail">Rol: {request.roleName}</span> : null}<span className="cell-detail">{requestSourceLabels[request.source]} · {formatDate(request.createdAt)}</span></td>
            <td>{request.justification ?? <span className="muted">—</span>}</td>
            <td><span className={`tag ${request.status === "Pending" ? "tag--warning" : ""}`}>{requestStatusLabels[request.status]}</span>
              {request.status === "Pending" && request.expiresAt ? <span className="cell-detail">Vence {formatDate(request.expiresAt)}</span> : null}
              {request.decidedBy ? <span className="cell-detail">{request.decidedBy.fullName} · {formatDate(request.decidedAt)}</span> : null}
              {request.decisionComment ? <span className="cell-detail">“{request.decisionComment}”</span> : null}</td>
            <td className="table-action">{canWrite && request.status === "Pending" && request.requester.id !== user.id
              ? <div className="button-group">
                <button className="button button--small" type="button" onClick={() => { decide.reset(); setDecision({ request, approve: true }); }}>Aprobar<span className="sr-only"> la solicitud de {request.requester.fullName}</span></button>
                <button className="button button--small button--danger-quiet" type="button" onClick={() => { decide.reset(); setDecision({ request, approve: false }); }}>Rechazar<span className="sr-only"> la solicitud de {request.requester.fullName}</span></button>
              </div> : null}</td>
          </tr>)}</tbody>
        </table>
      </div>
      <Pagination page={requests.data.page} pageSize={requests.data.pageSize} totalCount={requests.data.totalCount} totalPages={requests.data.totalPages} onPageChange={(value) => update("page", String(value), false)} onPageSizeChange={(value) => update("pageSize", String(value), false)} />
    </> : null}
    <DecisionDialog
      key={decision ? `${decision.request.id}-${decision.approve}` : "none"}
      open={decision !== null}
      title={decision?.approve ? "Aprobar la solicitud" : "Rechazar la solicitud"}
      detail={decision ? `${decision.request.requester.fullName} ${decision.approve ? "tendrá" : "no tendrá"} acceso a ${decision.request.applicationName}${decision.request.roleName ? ` con el rol ${decision.request.roleName}` : ""}. Le avisaremos por correo.` : ""}
      confirmLabel={decision?.approve ? "Aprobar" : "Rechazar"}
      commentLabel={decision?.approve ? "Comentario (opcional)" : "Motivo"}
      commentRequired={decision?.approve === false}
      dangerous={decision?.approve === false}
      busy={decide.isPending}
      error={decide.error ? errorMessage(decide.error, governanceMessages) : null}
      onCancel={() => setDecision(null)}
      onConfirm={(comment) => { if (decision) decide.mutate({ ...decision, comment }); }}
    />
  </>;
}
