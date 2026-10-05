import { countPath, integrationLinks } from "./integrations";

const id = "11111111-1111-4111-8111-111111111111";

describe("integrationLinks", () => {
  it("links each piece of the application with the filter its list expects", () => {
    const all = new Set(["AUTHCENTER_OAUTH_CLIENTS_READ", "AUTHCENTER_SAML_APPS_READ", "AUTHCENTER_USERS_READ", "AUTHCENTER_ACCESS_POLICIES_READ", "AUTHCENTER_FEDERATION_READ"]);
    expect(integrationLinks(id, all).map((link) => link.to)).toEqual([
      `/oauth-clients?applicationId=${id}`,
      `/saml-apps?application=${id}`,
      `/users?application=${id}`,
      `/access-policies/${id}`,
      `/federation?applicationId=${id}`
    ]);
  });

  it("offers only what the operator may open", () => {
    expect(integrationLinks(id, new Set(["AUTHCENTER_USERS_READ"])).map((link) => link.label)).toEqual(["Personas con acceso"]);
    expect(integrationLinks(id, new Set())).toEqual([]);
  });
});

describe("countPath", () => {
  it("asks each list for one item of the application: its total is the count", () => {
    expect(countPath("oauth-clients", id)).toBe(`/api/oauth/clients?applicationSystemId=${id}&page=1&pageSize=1`);
    expect(countPath("saml-apps", id)).toBe(`/api/saml/service-providers?applicationSystemId=${id}&page=1&pageSize=1`);
  });
});
