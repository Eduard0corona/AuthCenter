import { useQuery } from "@tanstack/react-query";
import { Link, useSearchParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { DirectoryGroupSummary, DynamicGroupRule, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { buildQuery, formatDate } from "../../utils/format";
import { describeRule } from "./group-rule";

export default function GroupRulesPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_GROUPS_WRITE");
  const [params, setParams] = useSearchParams();
  const page = Math.max(Number(params.get("page") ?? 1), 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const groupId = params.get("groupId") ?? "";
  const active = params.get("active") ?? "";
  const groups = useQuery({
    queryKey: ["groups", "group-rule-filter"],
    queryFn: ({ signal }) => fetchAllAsPage<DirectoryGroupSummary>("/api/groups", signal)
  });
  const rules = useQuery({
    queryKey: ["group-rules", page, pageSize, groupId, active],
    queryFn: ({ signal }) => apiRequest<PagedResult<DynamicGroupRule>>(`/api/lifecycle/group-rules?${buildQuery({
      page, pageSize, directoryGroupId: groupId || null, isActive: active === "" ? null : active === "true"
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
    <PageHeader eyebrow="Automatización" title="Reglas de grupo" description="Reglas de membresía dinámica basadas en atributos del perfil universal, con vista previa de los usuarios afectados." actions={canWrite ? <Link className="button" to="/group-rules/new">Nueva regla</Link> : undefined} />
    <section className="toolbar" aria-label="Filtros de reglas de grupo">
      <label className="field"><span>Grupo</span><select value={groupId} onChange={(event) => update("groupId", event.target.value)}><option value="">Todos</option>{groups.data?.items.map((group) => <option key={group.id} value={group.id}>{group.name}</option>)}</select></label>
      <label className="field"><span>Estado</span><select value={active} onChange={(event) => update("active", event.target.value)}><option value="">Todos</option><option value="true">Activas</option><option value="false">Inactivas</option></select></label>
    </section>
    {rules.isPending ? <PageState title="Cargando reglas de grupo" busy /> : null}
    {rules.isError ? <PageState title="No pudimos cargar las reglas de grupo" detail={errorMessage(rules.error)} tone="error" action={<button className="button" onClick={() => void rules.refetch()}>Reintentar</button>} /> : null}
    {rules.data && rules.data.items.length === 0 ? <PageState title="No hay reglas de grupo" detail="Ajusta los filtros o crea la primera regla de membresía dinámica." /> : null}
    {rules.data?.items.length ? <>
      <div className="data-table" tabIndex={0} role="region" aria-label="Reglas de grupo, desplazamiento horizontal"><table><caption className="sr-only">Reglas de grupo</caption><thead><tr><th>Grupo</th><th>Condición</th><th>Creada</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{rules.data.items.map((rule) => <tr key={rule.id}><td><strong>{rule.groupName}</strong></td><td><span className="mono">{describeRule(rule)}</span></td><td>{formatDate(rule.createdAt)}</td><td><StatusBadge active={rule.isActive} activeLabel="Activa" inactiveLabel="Inactiva" /></td><td className="table-action"><Link className="button button--small button--secondary" to={`/group-rules/${rule.id}`}>{canWrite ? "Editar" : "Consultar"}</Link></td></tr>)}</tbody></table></div>
      <Pagination page={rules.data.page} pageSize={rules.data.pageSize} totalCount={rules.data.totalCount} totalPages={rules.data.totalPages} onPageChange={(value) => update("page", String(value))} onPageSizeChange={(value) => update("pageSize", String(value))} />
    </> : null}
  </>;
}

