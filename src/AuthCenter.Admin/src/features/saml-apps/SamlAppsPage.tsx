import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useCallback } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useApplicationsCatalog } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { PagedResult, SamlIdentityProvider, SamlServiceProvider } from "../../api/types";
import { useSession } from "../../auth/session";
import { DebouncedTextField } from "../../components/DebouncedTextField";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { buildQuery, formatDate } from "../../utils/format";

export default function SamlAppsPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_SAML_APPS_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const search = params.get("search") ?? "";
  const applicationSystemId = params.get("application") ?? "";
  const active = params.get("active") ?? "";
  const applications = useApplicationsCatalog(canReadApplications);
  const providers = useQuery({
    queryKey: ["saml-apps", search, applicationSystemId, active, page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => apiRequest<PagedResult<SamlServiceProvider>>(`/api/saml/service-providers?${buildQuery({ page, pageSize, search, applicationSystemId, isActive: active === "" ? null : active === "true" })}`, { signal })
  });
  const update = useCallback((name: string, value: string, replace = true) => setParams((current) => {
    const next = new URLSearchParams(current);
    if (value) next.set(name, value); else next.delete(name);
    if (name !== "page") next.delete("page");
    return next;
  }, { replace }), [setParams]);
  const commitSearch = useCallback((value: string) => update("search", value), [update]);

  return <>
    <PageHeader
      eyebrow="Aplicaciones"
      title="Aplicaciones SAML"
      description="Aplicaciones que inician sesión con SAML 2.0 y AuthCenter como proveedor de identidad: el acceso, la política y el MFA de su aplicación se aplican a cada aserción."
      actions={canWrite && canReadApplications ? <Link className="button" to="/saml-apps/new">Nueva aplicación SAML</Link> : undefined}
    />
    <IdentityProviderPanel />
    <section className="toolbar" aria-label="Filtros de aplicaciones SAML">
      <DebouncedTextField label="Buscar" value={search} onCommit={commitSearch} placeholder="Nombre o entity ID" />
      {canReadApplications ? <label className="field"><span>Aplicación</span><select value={applicationSystemId} onChange={(event) => update("application", event.target.value, false)}><option value="">Todas</option>{applications.data?.map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></label> : null}
      <label className="field"><span>Estado</span><select value={active} onChange={(event) => update("active", event.target.value, false)}><option value="">Todos</option><option value="true">Activas</option><option value="false">Inactivas</option></select></label>
    </section>
    {providers.isPending ? <PageState title="Cargando aplicaciones SAML" busy /> : null}
    {providers.isError ? <PageState title="No pudimos cargar las aplicaciones SAML" detail={errorMessage(providers.error)} tone="error" action={<button className="button" type="button" onClick={() => void providers.refetch()}>Reintentar</button>} /> : null}
    {providers.data && providers.data.items.length === 0 ? <PageState title="Sin aplicaciones SAML" detail={search || applicationSystemId || active ? "No encontramos aplicaciones con estos filtros." : "Registra una aplicación SAML con sus metadatos para que sus usuarios inicien sesión con AuthCenter."} /> : null}
    {providers.data && providers.data.items.length > 0 ? <>
      <div className="data-table" tabIndex={0} role="region" aria-label="Aplicaciones SAML, desplazamiento horizontal" aria-busy={providers.isFetching || undefined}>
        <table>
          <caption className="sr-only">Aplicaciones SAML</caption>
          <thead><tr><th scope="col">Aplicación SAML</th><th scope="col">Aplicación de AuthCenter</th><th scope="col">Actualizada</th><th scope="col">Estado</th><th scope="col"><span className="sr-only">Acciones</span></th></tr></thead>
          <tbody>{providers.data.items.map((provider) => <tr key={provider.id}>
            <td><strong>{provider.name}</strong><span className="cell-detail mono">{provider.entityId}</span></td>
            <td>{provider.applicationName}<span className="cell-detail mono">{provider.applicationCode}</span></td>
            <td>{formatDate(provider.updatedAt ?? provider.createdAt)}</td>
            <td><StatusBadge active={provider.isActive} activeLabel="Activa" inactiveLabel="Inactiva" /></td>
            <td className="table-action"><Link className="button button--small button--secondary" to={`/saml-apps/${provider.id}`}>{canWrite ? "Editar" : "Consultar"}</Link></td>
          </tr>)}</tbody>
        </table>
      </div>
      <Pagination page={providers.data.page} pageSize={providers.data.pageSize} totalCount={providers.data.totalCount} totalPages={providers.data.totalPages} onPageChange={(value) => update("page", String(value), false)} onPageSizeChange={(value) => update("pageSize", String(value), false)} />
    </> : null}
  </>;
}

/** What each SAML application registers about AuthCenter: its entity ID, endpoints and certificate. */
export function IdentityProviderPanel() {
  const identityProvider = useQuery({
    queryKey: ["saml-identity-provider"],
    queryFn: ({ signal }) => apiRequest<SamlIdentityProvider>("/api/saml/identity-provider", { signal })
  });
  const info = identityProvider.data;
  return <section className="settings-panel" aria-labelledby="saml-idp">
    <div className="settings-panel__heading"><div><h2 id="saml-idp">AuthCenter como proveedor de identidad</h2><p>Registra estos datos en cada aplicación SAML, o dale la URL de metadatos.</p></div>{info ? <StatusBadge active={info.isConfigured} activeLabel="Configurado" inactiveLabel="Sin configurar" /> : null}</div>
    {identityProvider.isError ? <p className="alert alert--error" role="alert">{errorMessage(identityProvider.error)}</p> : null}
    {info && !info.isConfigured ? <p className="alert alert--error" role="alert">Las aplicaciones SAML no pueden iniciar sesión: {info.problem ?? "falta el certificado SAML."}</p> : null}
    {info ? <dl className="profile-summary">
      <div><dt>URL de metadatos</dt><dd className="mono">{info.metadataUrl}</dd></div>
      <div><dt>Entity ID</dt><dd className="mono">{info.entityId}</dd></div>
      <div><dt>Inicio de sesión (SSO)</dt><dd className="mono">{info.singleSignOnUrl}</dd></div>
      <div><dt>Cierre de sesión (SLO)</dt><dd className="mono">{info.singleLogoutUrl}</dd></div>
      {info.certificate ? <>
        <div><dt>Certificado de firma (SHA-256)</dt><dd className="mono">{info.certificate.thumbprintSha256}</dd></div>
        <div><dt>Certificado vigente hasta</dt><dd>{formatDate(info.certificate.notAfter)}</dd></div>
      </> : null}
    </dl> : null}
  </section>;
}
