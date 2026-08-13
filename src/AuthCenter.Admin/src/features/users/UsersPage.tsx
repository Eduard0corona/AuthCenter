import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { apiRequest, ApiError } from "../../api/client";
import type { PagedResult, UserSummary } from "../../api/types";
import { useSession } from "../../auth/session";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { useDebouncedValue } from "../../hooks/useDebouncedValue";
import { buildQuery, formatDate } from "../../utils/format";

export default function UsersPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_USERS_WRITE");
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const activeFilter = params.get("active") ?? "";
  const sort = ["name-asc", "name-desc", "email-asc", "email-desc", "createdAt-desc", "createdAt-asc", "lastLoginAt-desc", "lastLoginAt-asc"].includes(params.get("sort") ?? "") ? params.get("sort")! : "name-asc";
  const [sortBy, sortDirection] = sort.split("-");
  const [search, setSearch] = useState(params.get("search") ?? "");
  const debouncedSearch = useDebouncedValue(search);
  const [target, setTarget] = useState<UserSummary | null>(null);
  const [feedback, setFeedback] = useState("");

  useEffect(() => {
    setParams((current) => {
      const next = new URLSearchParams(current);
      if (debouncedSearch) next.set("search", debouncedSearch); else next.delete("search");
      next.set("page", "1");
      return next;
    }, { replace: true });
  }, [debouncedSearch, setParams]);

  const users = useQuery({
    queryKey: ["users", page, pageSize, params.get("search") ?? "", activeFilter, sort],
    queryFn: ({ signal }) => apiRequest<PagedResult<UserSummary>>(`/api/users?${buildQuery({
      page,
      pageSize,
      search: params.get("search"),
      isActive: activeFilter === "" ? null : activeFilter === "true",
      sortBy: sortBy === "name" ? "fullName" : sortBy,
      sortDirection
    })}`, { signal })
  });
  const changeStatus = useMutation({
    mutationFn: (user: UserSummary) => apiRequest<void>(`/api/users/${user.id}/${user.isActive ? "deactivate" : "activate"}`, { method: "PATCH" }),
    onSuccess: async (_, user) => {
      setFeedback(`${user.fullName} ahora está ${user.isActive ? "inactivo" : "activo"}.`);
      setTarget(null);
      await queryClient.invalidateQueries({ queryKey: ["users"] });
    }
  });

  function updateParam(name: string, value: string): void {
    setParams((current) => {
      const next = new URLSearchParams(current);
      if (value) next.set(name, value); else next.delete(name);
      if (name !== "page") next.set("page", "1");
      return next;
    });
  }

  return (
    <>
      <PageHeader eyebrow="Directorio" title="Usuarios" description="Consulta y administra el estado del directorio sin descargarlo completo." actions={canWrite ? <span className="button-group"><Link className="button button--secondary" to="/users/invite">Invitar usuario</Link><Link className="button" to="/users/new">Crear usuario</Link></span> : null} />
      <section className="toolbar" aria-label="Filtros de usuarios">
        <label className="field field--search"><span>Buscar</span><input type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Nombre o correo" /></label>
        <label className="field"><span>Estado</span><select value={activeFilter} onChange={(event) => updateParam("active", event.target.value)}><option value="">Todos</option><option value="true">Activos</option><option value="false">Inactivos</option></select></label>
        <label className="field"><span>Orden</span><select value={sort} onChange={(event) => updateParam("sort", event.target.value)}><option value="name-asc">Nombre A–Z</option><option value="name-desc">Nombre Z–A</option><option value="email-asc">Correo A–Z</option><option value="email-desc">Correo Z–A</option><option value="createdAt-desc">Más recientes</option><option value="createdAt-asc">Más antiguos</option><option value="lastLoginAt-desc">Acceso más reciente</option><option value="lastLoginAt-asc">Acceso más antiguo</option></select></label>
      </section>
      {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
      {changeStatus.error ? <p className="alert alert--error" role="alert">{errorMessage(changeStatus.error)}</p> : null}
      {users.isPending ? <PageState title="Cargando usuarios" busy /> : null}
      {users.isError ? <PageState title="No pudimos cargar usuarios" detail={errorMessage(users.error)} tone="error" action={<button className="button" type="button" onClick={() => void users.refetch()}>Reintentar</button>} /> : null}
      {users.data && users.data.items.length === 0 ? <PageState title="No hay resultados" detail="Ajusta la búsqueda o los filtros." /> : null}
      {users.data && users.data.items.length > 0 ? (
        <>
          <div className="data-table" tabIndex={0} role="region" aria-label="Usuarios del directorio, desplazamiento horizontal"><table><caption className="sr-only">Usuarios del directorio</caption><thead><tr><th scope="col">Usuario</th><th scope="col">Estado</th><th scope="col">Roles</th><th scope="col">Último acceso</th><th scope="col"><span className="sr-only">Acciones</span></th></tr></thead><tbody>
            {users.data.items.map((user) => <tr key={user.id}>
              <td><strong>{user.fullName}</strong><span className="cell-detail">{user.email}</span></td>
              <td><StatusBadge active={user.isActive} /></td>
              <td>{user.roles.length > 0 ? user.roles.slice(0, 2).map((role) => <span className="tag" key={role}>{role}</span>) : <span className="muted">Sin roles</span>}</td>
              <td>{formatDate(user.lastLoginAt)}</td>
              <td className="table-action"><span className="button-group"><Link className="button button--small button--secondary" to={`/users/${user.id}`}>{canWrite ? "Administrar" : "Consultar"}</Link>{canWrite ? <button className={`button button--small ${user.isActive ? "button--danger-quiet" : "button--secondary"}`} type="button" onClick={() => setTarget(user)}>{user.isActive ? "Desactivar" : "Activar"}</button> : null}</span></td>
            </tr>)}
          </tbody></table></div>
          <Pagination page={users.data.page} pageSize={users.data.pageSize} totalCount={users.data.totalCount} totalPages={users.data.totalPages} onPageChange={(value) => updateParam("page", String(value))} onPageSizeChange={(value) => updateParam("pageSize", String(value))} />
        </>
      ) : null}
      <ConfirmDialog open={target !== null} title={target?.isActive ? "Desactivar usuario" : "Activar usuario"} detail={target ? `${target.isActive ? "Desactivar" : "Activar"} a ${target.fullName} (${target.email}). La API volverá a validar tus permisos.` : ""} confirmLabel={target?.isActive ? "Desactivar" : "Activar"} dangerous={Boolean(target?.isActive)} busy={changeStatus.isPending} onCancel={() => setTarget(null)} onConfirm={() => { if (target) changeStatus.mutate(target); }} />
    </>
  );
}

function errorMessage(error: unknown): string {
  return error instanceof ApiError ? error.message : "Ocurrió un error inesperado.";
}
