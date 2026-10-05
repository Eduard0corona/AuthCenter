import { buildQuery } from "../../utils/format";

export type IntegrationKey = "oauth-clients" | "saml-apps" | "people" | "policy" | "federation";

export interface IntegrationLink {
  key: IntegrationKey;
  label: string;
  detail: string;
  /** The list or page, already narrowed to the application (each list names its filter its own way). */
  to: string;
  /** What it takes to open it. */
  permission: string;
}

/** Where an application's pieces live, among those the operator may open. */
export function integrationLinks(applicationId: string, granted: ReadonlySet<string>): IntegrationLink[] {
  const id = encodeURIComponent(applicationId);
  const links: IntegrationLink[] = [
    { key: "oauth-clients", label: "Clientes OAuth", detail: "Inicio de sesión con OpenID Connect", to: `/oauth-clients?applicationId=${id}`, permission: "AUTHCENTER_OAUTH_CLIENTS_READ" },
    { key: "saml-apps", label: "Aplicaciones SAML", detail: "Inicio de sesión con SAML 2.0", to: `/saml-apps?application=${id}`, permission: "AUTHCENTER_SAML_APPS_READ" },
    { key: "people", label: "Personas con acceso", detail: "Directo o por grupo", to: `/users?application=${id}`, permission: "AUTHCENTER_USERS_READ" },
    { key: "policy", label: "Política de acceso", detail: "Quién entra y con qué verificación", to: `/access-policies/${id}`, permission: "AUTHCENTER_ACCESS_POLICIES_READ" },
    { key: "federation", label: "Federación", detail: "Cuentas de trabajo de otra organización", to: `/federation?applicationId=${id}`, permission: "AUTHCENTER_FEDERATION_READ" }
  ];
  return links.filter((link) => granted.has(link.permission));
}

/** One item per page is enough: the list's total is the count. */
export function countPath(key: "oauth-clients" | "saml-apps", applicationId: string): string {
  const query = buildQuery({ applicationSystemId: applicationId, page: 1, pageSize: 1 });
  return key === "oauth-clients" ? `/api/oauth/clients?${query}` : `/api/saml/service-providers?${query}`;
}
