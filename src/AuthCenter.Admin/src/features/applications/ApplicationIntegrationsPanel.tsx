import { useQuery, type UseQueryResult } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { apiRequest } from "../../api/client";
import type { ApplicationSummary, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { countPath, integrationLinks, type IntegrationKey } from "./integrations";

/** What an application is connected to and who can enter, each a link to the narrowed list (ADM-UX-06). */
export function ApplicationIntegrationsPanel({ application }: { application: ApplicationSummary }) {
  const { permissions } = useSession();
  const links = integrationLinks(application.id, permissions);
  const canReadClients = permissions.has("AUTHCENTER_OAUTH_CLIENTS_READ");
  const canReadSaml = permissions.has("AUTHCENTER_SAML_APPS_READ");
  // The query keys start with each list's key, so creating a client refreshes the count.
  const clients = useQuery({
    queryKey: ["oauth-clients", "count", application.id],
    enabled: canReadClients,
    queryFn: ({ signal }) => apiRequest<PagedResult<unknown>>(countPath("oauth-clients", application.id), { signal }),
    select: (page) => page.totalCount
  });
  const samlApps = useQuery({
    queryKey: ["saml-apps", "count", application.id],
    enabled: canReadSaml,
    queryFn: ({ signal }) => apiRequest<PagedResult<unknown>>(countPath("saml-apps", application.id), { signal }),
    select: (page) => page.totalCount
  });
  const counts: Partial<Record<IntegrationKey, UseQueryResult<number>>> = { "oauth-clients": clients, "saml-apps": samlApps };
  const canCreateClient = permissions.has("AUTHCENTER_OAUTH_CLIENTS_WRITE") && application.isActive;
  const withoutClients = canReadClients && canReadSaml && clients.data === 0 && samlApps.data === 0;
  if (links.length === 0 && !canCreateClient) return null;

  return (
    <section className="settings-panel" aria-labelledby="application-integrations">
      <div className="settings-panel__heading">
        <div><h2 id="application-integrations">Integraciones y acceso</h2><p>Cómo inician sesión sus usuarios, quién puede entrar y con qué reglas.</p></div>
        {canCreateClient ? <Link className="button" to={`/oauth-clients/new?applicationId=${encodeURIComponent(application.id)}`}>Nuevo cliente OAuth</Link> : null}
      </div>
      {withoutClients ? <p className="alert alert--info">Todavía no tiene clientes: crea uno para que sus usuarios puedan iniciar sesión.</p> : null}
      {links.length > 0 ? (
        <ul className="metric-grid">
          {links.map((link) => {
            const count = counts[link.key];
            return (
              <li key={link.key}>
                <Link className="metric-card" to={link.to}>
                  {count ? <span className="metric-card__value">{count.isSuccess ? count.data.toLocaleString("es-MX") : count.isError ? "—" : "…"}</span> : null}
                  <span className="metric-card__label">{link.label}</span>
                  <span className="metric-card__detail">{link.detail}</span>
                </Link>
              </li>
            );
          })}
        </ul>
      ) : null}
    </section>
  );
}
