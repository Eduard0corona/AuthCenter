import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { apiRequest, ApiError } from "../../api/client";
import type { DirectoryGroupSummary, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { useDebouncedValue } from "../../hooks/useDebouncedValue";
import { buildQuery } from "../../utils/format";

export default function GroupsPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_GROUPS_WRITE");
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const active = params.get("active") ?? "";
  const [search, setSearch] = useState(params.get("search") ?? "");
  const debouncedSearch = useDebouncedValue(search);
  useEffect(() => { setParams((current) => { const next = new URLSearchParams(current); if (debouncedSearch) next.set("search", debouncedSearch); else next.delete("search"); next.set("page", "1"); return next; }, { replace: true }); }, [debouncedSearch, setParams]);
  const groups = useQuery({ queryKey: ["groups", page, pageSize, params.get("search") ?? "", active], queryFn: ({ signal }) => apiRequest<PagedResult<DirectoryGroupSummary>>(`/api/groups?${buildQuery({ page, pageSize, search: params.get("search"), isActive: active === "" ? null : active === "true" })}`, { signal }) });
  function update(name: string, value: string): void { setParams((current) => { const next = new URLSearchParams(current); if (value) next.set(name, value); else next.delete(name); if (name !== "page") next.set("page", "1"); return next; }); }
  return <>
    <PageHeader eyebrow="Directorio" title="Grupos" description="Administra membresías y acceso heredado sin operar asignaciones una por una." actions={canWrite ? <Link className="button" to="/groups/new">Nuevo grupo</Link> : undefined} />
    <section className="toolbar" aria-label="Filtros de grupos"><label className="field"><span>Buscar</span><input type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Nombre del grupo" /></label><label className="field"><span>Estado</span><select value={active} onChange={(event) => update("active", event.target.value)}><option value="">Todos</option><option value="true">Activos</option><option value="false">Inactivos</option></select></label></section>
    {groups.isPending ? <PageState title="Cargando grupos" busy /> : null}{groups.isError ? <PageState title="No pudimos cargar los grupos" detail={message(groups.error)} tone="error" /> : null}{groups.data && groups.data.items.length === 0 ? <PageState title="No hay grupos" detail="Ajusta los filtros o crea un grupo." /> : null}
    {groups.data?.items.length ? <><div className="data-table" tabIndex={0} role="region" aria-label="Grupos, desplazamiento horizontal"><table><caption className="sr-only">Grupos</caption><thead><tr><th>Grupo</th><th>Miembros</th><th>Aplicaciones</th><th>Roles heredados</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{groups.data.items.map((group) => <tr key={group.id}><td><strong>{group.name}</strong><span className="cell-detail">{group.description ?? "Sin descripción"}</span></td><td>{group.memberCount}</td><td>{group.applications.length}</td><td>{group.roles.length}</td><td><StatusBadge active={group.isActive} /></td><td className="table-action"><Link className="button button--small button--secondary" to={`/groups/${group.id}`}>{canWrite ? "Administrar" : "Consultar"}</Link></td></tr>)}</tbody></table></div><Pagination page={groups.data.page} pageSize={groups.data.pageSize} totalCount={groups.data.totalCount} totalPages={groups.data.totalPages} onPageChange={(value) => update("page", String(value))} onPageSizeChange={(value) => update("pageSize", String(value))} /></> : null}
  </>;
}
function message(error: unknown): string { return error instanceof ApiError ? error.message : "Ocurrió un error inesperado."; }
