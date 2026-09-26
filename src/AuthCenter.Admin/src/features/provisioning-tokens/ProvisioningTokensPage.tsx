import { useQuery } from "@tanstack/react-query";
import { Link, useSearchParams } from "react-router-dom";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, PagedResult, ProvisioningTokenMetadata } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { buildQuery, formatDate } from "../../utils/format";

export default function ProvisioningTokensPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_PROVISIONING_WRITE");
  const [params, setParams] = useSearchParams();
  const page = Math.max(Number(params.get("page") ?? 1), 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const applicationId = params.get("applicationId") ?? "";
  const status = params.get("status") ?? "";
  const applications = useQuery({
    queryKey: ["applications", "provisioning-token-filter"],
    queryFn: ({ signal }) => apiRequest<PagedResult<ApplicationSummary>>("/api/applications?page=1&pageSize=100", { signal })
  });
  const tokens = useQuery({
    queryKey: ["provisioning-tokens", page, pageSize, applicationId, status],
    queryFn: ({ signal }) => apiRequest<PagedResult<ProvisioningTokenMetadata>>(`/api/provisioning-tokens?${buildQuery({
      page, pageSize, applicationSystemId: applicationId || null, status: status || null
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

  return <>
    <PageHeader eyebrow="Integraciones" title="Provisioning tokens" description="Administra credenciales SCIM con scopes mínimos, expiración obligatoria y revelado único." actions={canWrite ? <Link className="button" to="/provisioning-tokens/new">Nuevo token</Link> : undefined} />
    <section className="toolbar" aria-label="Filtros de provisioning tokens">
      <label className="field"><span>Aplicación</span><select value={applicationId} onChange={(event) => update("applicationId", event.target.value)}><option value="">Todas</option>{applications.data?.items.map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></label>
      <label className="field"><span>Estado</span><select value={status} onChange={(event) => update("status", event.target.value)}><option value="">Todos</option><option value="active">Activos</option><option value="expired">Expirados</option><option value="revoked">Revocados</option></select></label>
    </section>
    {tokens.isPending ? <PageState title="Cargando provisioning tokens" busy /> : null}
    {tokens.isError ? <PageState title="No pudimos cargar los provisioning tokens" detail={errorMessage(tokens.error)} tone="error" action={<button className="button" onClick={() => void tokens.refetch()}>Reintentar</button>} /> : null}
    {tokens.data && tokens.data.items.length === 0 ? <PageState title="No hay provisioning tokens" detail="Ajusta los filtros o crea la primera credencial SCIM." /> : null}
    {tokens.data?.items.length ? <>
      <div className="data-table" tabIndex={0} role="region" aria-label="Provisioning tokens, desplazamiento horizontal"><table><caption className="sr-only">Provisioning tokens</caption><thead><tr><th>Token</th><th>Aplicación</th><th>Scopes</th><th>Expiración</th><th>Último uso</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{tokens.data.items.map((token) => <tr key={token.id}><td><strong>{token.name}</strong><span className="cell-detail mono">{token.id}</span></td><td>{token.applicationName}</td><td><span className="cell-detail">{token.scopes.join(", ")}</span></td><td>{formatDate(token.expiresAt)}</td><td>{formatDate(token.lastUsedAt)}</td><td><TokenStatus status={token.status} /></td><td className="table-action"><Link className="button button--small button--secondary" to={`/provisioning-tokens/${token.id}`}>{canWrite ? "Administrar" : "Consultar"}</Link></td></tr>)}</tbody></table></div>
      <Pagination page={tokens.data.page} pageSize={tokens.data.pageSize} totalCount={tokens.data.totalCount} totalPages={tokens.data.totalPages} onPageChange={(value) => update("page", String(value))} onPageSizeChange={(value) => update("pageSize", String(value))} />
    </> : null}
  </>;
}

export function TokenStatus({ status }: { status: ProvisioningTokenMetadata["status"] }) {
  const labels = { active: "Activo", expired: "Expirado", revoked: "Revocado" } as const;
  return <StatusBadge active={status === "active"} activeLabel={labels[status]} inactiveLabel={labels[status]} />;
}

