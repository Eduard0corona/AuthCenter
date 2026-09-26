import type { Page, Route } from "@playwright/test";

export const allPermissions = [
  "AUTHCENTER_USERS_READ", "AUTHCENTER_USERS_WRITE", "AUTHCENTER_APPLICATIONS_READ", "AUTHCENTER_APPLICATIONS_WRITE",
  "AUTHCENTER_ROLES_READ", "AUTHCENTER_ROLES_WRITE", "AUTHCENTER_PERMISSIONS_READ", "AUTHCENTER_PERMISSIONS_WRITE",
  "AUTHCENTER_GROUPS_READ", "AUTHCENTER_GROUPS_WRITE", "AUTHCENTER_PROFILE_SCHEMAS_READ", "AUTHCENTER_PROFILE_SCHEMAS_WRITE",
  "AUTHCENTER_OAUTH_CLIENTS_READ", "AUTHCENTER_OAUTH_CLIENTS_WRITE", "AUTHCENTER_ACCESS_POLICIES_READ", "AUTHCENTER_ACCESS_POLICIES_WRITE",
  "AUTHCENTER_AUDIT_LOGS_READ", "AUTHCENTER_FEDERATION_READ", "AUTHCENTER_FEDERATION_WRITE",
  "AUTHCENTER_PROVISIONING_READ", "AUTHCENTER_PROVISIONING_WRITE", "AUTHCENTER_EVENT_HOOKS_READ", "AUTHCENTER_EVENT_HOOKS_WRITE",
  "AUTHCENTER_SAML_APPS_READ", "AUTHCENTER_SAML_APPS_WRITE"
];

export const applicationId = "11111111-1111-4111-8111-111111111111";

export function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(status < 400 ? { success: true, data } : data) });
}

export function paged<T>(items: T[], page = 1, pageSize = 20) {
  return { items, totalCount: items.length, page, pageSize, totalPages: Math.max(1, Math.ceil(items.length / pageSize)) };
}

/** The console's own calls: session, metadata, version and the overview metrics. */
export async function mockShell(page: Page, permissions: string[] = allPermissions, environmentName = "Production") {
  await page.route("**/ui-api/session", (route) => json(route, {
    user: { id: "operator-1", name: "Ada Operadora", email: "ada@example.test", applications: ["AUTHCENTER"], roles: ["Admin"], permissions },
    csrfToken: "e2e-csrf"
  }));
  await page.route("**/api/admin-metadata", (route) => json(route, { errorCodes: {}, stepUpPurposes: {}, operationPermissions: {}, maximumPageSize: 100, environmentName }));
  await page.route("**/api/version", (route) => json(route, { version: "1.4.0", commit: "0123456789abcdef", adminFrontendBasePath: "/admin-v2", contractVersion: 1 }));
}

/** A password step-up that records the purpose of each proof. */
export function mockStepUp(page: Page, onPurpose: (purpose: string) => void = () => undefined) {
  return page.route("**/api/auth/reauth/password", async (route) => {
    onPurpose((route.request().postDataJSON() as { purpose: string }).purpose);
    await json(route, { proofToken: "single-use-proof", assuranceLevel: "Password", expiresIn: 300 });
  });
}
