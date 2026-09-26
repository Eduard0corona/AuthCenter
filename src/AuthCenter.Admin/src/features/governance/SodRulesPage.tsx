import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useCallback, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { PagedResult, SeparationOfDutiesRule, SeparationOfDutiesViolation } from "../../api/types";
import { useSession } from "../../auth/session";
import { DebouncedTextField } from "../../components/DebouncedTextField";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { buildQuery } from "../../utils/format";
import { describeHolding } from "./governance";

export default function SodRulesPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_GOVERNANCE_WRITE");
  const canReadRoles = permissions.has("AUTHCENTER_ROLES_READ");
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const search = params.get("search") ?? "";
  const [violationsPage, setViolationsPage] = useState(1);
  const [violationsPageSize, setViolationsPageSize] = useState(20);
  const rules = useQuery({
    queryKey: ["sod-rules", search, page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => apiRequest<PagedResult<SeparationOfDutiesRule>>(`/api/governance/sod-rules?${buildQuery({ page, pageSize, search })}`, { signal })
  });
  const violations = useQuery({
    queryKey: ["sod-violations", violationsPage, violationsPageSize],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => apiRequest<PagedResult<SeparationOfDutiesViolation>>(`/api/governance/sod-violations?${buildQuery({ page: violationsPage, pageSize: violationsPageSize })}`, { signal })
  });
  const update = useCallback((name: string, value: string) => setParams((current) => {
    const next = new URLSearchParams(current);
    if (value) next.set(name, value); else next.delete(name);
    if (name !== "page") next.delete("page");
    return next;
  }, { replace: true }), [setParams]);
  const commitSearch = useCallback((value: string) => update("search", value), [update]);

  return <>
    <PageHeader
      eyebrow="Gobierno"
      title="Segregación de funciones"
      description="Pares de roles que nadie debe tener a la vez. AuthCenter rechaza las asignaciones, membresías y aprobaciones que los juntarían; lo que llega por SCIM, reglas de grupo o federación se reporta abajo."
      actions={canWrite && canReadRoles ? <Link className="button" to="/sod-rules/new">Nueva regla</Link> : undefined}
    />
    <section className="toolbar" aria-label="Filtros de reglas">
      <DebouncedTextField label="Buscar" value={search} onCommit={commitSearch} placeholder="Regla o rol" />
    </section>
    {rules.isPending ? <PageState title="Cargando reglas" busy /> : null}
    {rules.isError ? <PageState title="No pudimos cargar las reglas" detail={errorMessage(rules.error)} tone="error" action={<button className="button" type="button" onClick={() => void rules.refetch()}>Reintentar</button>} /> : null}
    {rules.data && rules.data.items.length === 0 ? <PageState title="Sin reglas" detail={search ? "Ninguna regla coincide." : "Crea una regla para impedir que alguien tenga dos roles incompatibles, por ejemplo solicitar y aprobar pagos."} /> : null}
    {rules.data && rules.data.items.length > 0 ? <>
      <div className="data-table" tabIndex={0} role="region" aria-label="Reglas de segregación de funciones, desplazamiento horizontal" aria-busy={rules.isFetching || undefined}>
        <table>
          <caption className="sr-only">Reglas de segregación de funciones</caption>
          <thead><tr><th scope="col">Regla</th><th scope="col">Roles incompatibles</th><th scope="col">Violaciones</th><th scope="col">Estado</th><th scope="col"><span className="sr-only">Acciones</span></th></tr></thead>
          <tbody>{rules.data.items.map((rule) => <tr key={rule.id}>
            <td><strong>{rule.name}</strong>{rule.description ? <span className="cell-detail">{rule.description}</span> : null}</td>
            <td>{roleLabel(rule.firstRole)}<span className="cell-detail">y {roleLabel(rule.secondRole)}</span></td>
            <td>{rule.violationCount > 0 ? <span className="tag tag--warning">{rule.violationCount}</span> : "0"}</td>
            <td><StatusBadge active={rule.isActive} activeLabel="Activa" inactiveLabel="Inactiva" /></td>
            <td className="table-action"><Link className="button button--small button--secondary" to={`/sod-rules/${rule.id}`}>{canWrite ? "Editar" : "Consultar"}<span className="sr-only"> {rule.name}</span></Link></td>
          </tr>)}</tbody>
        </table>
      </div>
      <Pagination page={rules.data.page} pageSize={rules.data.pageSize} totalCount={rules.data.totalCount} totalPages={rules.data.totalPages} onPageChange={(value) => update("page", String(value))} onPageSizeChange={(value) => update("pageSize", String(value))} />
    </> : null}
    <section className="settings-panel" aria-labelledby="sod-violations">
      <div className="settings-panel__heading"><div><h2 id="sod-violations">Violaciones actuales</h2><p>Usuarios activos que tienen los dos roles de una regla activa, y cómo los tienen. Quita un rol directo o la membresía del grupo que lo da.</p></div>{violations.data ? <span className="tag">{violations.data.totalCount}</span> : null}</div>
      {violations.isPending ? <PageState title="Buscando violaciones" busy /> : null}
      {violations.isError ? <PageState title="No pudimos buscar las violaciones" detail={errorMessage(violations.error)} tone="error" action={<button className="button" type="button" onClick={() => void violations.refetch()}>Reintentar</button>} /> : null}
      {violations.data && violations.data.items.length === 0 ? <PageState title="Sin violaciones" detail="Nadie tiene a la vez los roles de una regla activa." /> : null}
      {violations.data && violations.data.items.length > 0 ? <>
        <div className="data-table" tabIndex={0} role="region" aria-label="Violaciones de segregación de funciones, desplazamiento horizontal">
          <table>
            <caption className="sr-only">Violaciones de segregación de funciones</caption>
            <thead><tr><th scope="col">Usuario</th><th scope="col">Regla</th><th scope="col">Cómo tiene los roles</th></tr></thead>
            <tbody>{violations.data.items.map((violation) => <tr key={`${violation.ruleId}-${violation.user.id}`}>
              <td><Link to={`/users/${violation.user.id}`}><strong>{violation.user.fullName}</strong></Link><span className="cell-detail">{violation.user.email}</span></td>
              <td><Link to={`/sod-rules/${violation.ruleId}`}>{violation.ruleName}</Link></td>
              <td>{describeHolding(violation.firstRole)}<span className="cell-detail">{describeHolding(violation.secondRole)}</span></td>
            </tr>)}</tbody>
          </table>
        </div>
        <Pagination page={violations.data.page} pageSize={violations.data.pageSize} totalCount={violations.data.totalCount} totalPages={violations.data.totalPages} onPageChange={setViolationsPage} onPageSizeChange={(value) => { setViolationsPageSize(value); setViolationsPage(1); }} />
      </> : null}
    </section>
  </>;
}

function roleLabel(role: SeparationOfDutiesRule["firstRole"]): string {
  return `${role.applicationCode ? `${role.applicationCode} · ` : ""}${role.name}${role.isActive ? "" : " (inactivo)"}`;
}
