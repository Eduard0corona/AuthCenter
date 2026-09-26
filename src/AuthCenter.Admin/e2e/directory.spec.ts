import { expect, test } from "@playwright/test";
import { applicationId, json, mockShell } from "./support";

const user = (index: number, pending = false) => ({
  id: `user-${index}`, fullName: `Usuario ${index}`, email: `usuario${index}@example.test`, pictureUrl: null, isActive: true, isExternalUser: false,
  createdAt: "2026-08-11T00:00:00Z", lastLoginAt: null, roles: [], applications: [],
  applicationAccesses: pending ? [{ applicationId, applicationCode: "TIENDITAPP", applicationName: "TienditApp", isActive: false, createdAt: "2026-09-25T00:00:00Z", revokedAt: null }] : []
});

test("filters users by pending access and application, and keeps a deep-linked page", async ({ page }) => {
  const requests: URL[] = [];
  await mockShell(page);
  // Two pages of applications: the selector must list both.
  await page.route("**/api/applications?**", (route) => {
    const url = new URL(route.request().url());
    const current = Number(url.searchParams.get("page"));
    const items = current === 1
      ? [{ id: applicationId, code: "TIENDITAPP", name: "TienditApp", description: null, isActive: true, createdAt: "2026-08-11T00:00:00Z", updatedAt: null, branding: null }]
      : [{ id: "22222222-0000-4000-8000-000000000002", code: "LEGACY", name: "Legacy Portal", description: null, isActive: true, createdAt: "2026-08-11T00:00:00Z", updatedAt: null, branding: null }];
    return json(route, { items, totalCount: 101, page: current, pageSize: 100, totalPages: 2 });
  });
  await page.route("**/api/users?**", (route) => {
    const url = new URL(route.request().url());
    requests.push(url);
    const pending = url.searchParams.get("hasPendingAccess") === "true";
    return json(route, { items: [user(3, true), ...(pending ? [] : [user(4)])], totalCount: 45, page: Number(url.searchParams.get("page")), pageSize: 20, totalPages: 3 });
  });

  await page.goto("/admin-v2/users?page=2");
  await expect(page.getByText("Usuario 3", { exact: true })).toBeVisible();
  await expect(page.getByText("Acceso pendiente")).toBeVisible();
  // Opening a deep link keeps its page instead of jumping back to the first one.
  await expect(page).toHaveURL(/page=2/);
  expect(requests.at(-1)?.searchParams.get("page")).toBe("2");

  await expect(page.getByRole("combobox", { name: "Aplicación", exact: true }).locator("option")).toHaveText(["Todas", "TienditApp", "Legacy Portal"]);
  await page.getByRole("combobox", { name: "Acceso", exact: true }).selectOption("true");
  await expect.poll(() => requests.at(-1)?.searchParams.get("hasPendingAccess")).toBe("true");
  expect(requests.at(-1)?.searchParams.get("page")).toBe("1");
  await page.getByRole("combobox", { name: "Aplicación", exact: true }).selectOption("22222222-0000-4000-8000-000000000002");
  await expect.poll(() => requests.at(-1)?.searchParams.get("applicationSystemId")).toBe("22222222-0000-4000-8000-000000000002");
  await expect(page).toHaveURL(/pendingAccess=true/);
});

test("the dashboard's pending-access metric opens the access requests waiting for a decision", async ({ page }) => {
  let lastQuery = "";
  await mockShell(page);
  await page.route("**/api/admin-dashboard", (route) => json(route, {
    generatedAt: "2026-09-26T12:00:00Z", activeUsers: 10, inactiveUsers: 0, activeApplications: 1, activeGroups: 0, pendingAccessRequests: 1, activeAccessReviews: 0, overdueAccessReviews: 0, pendingAccessReviewItems: 0, separationOfDutiesViolations: 0,
    activeFederationProviders: 0, expiringProvisioningTokens: 0, unverifiedEventHooks: 0, deadLetterDeliveries: 0, failedLoginsLast24Hours: 0, highRiskObservationsLast24Hours: 0
  }));
  await page.route("**/api/applications?**", (route) => json(route, { items: [], totalCount: 0, page: 1, pageSize: 100, totalPages: 0 }));
  await page.route("**/api/governance/access-requests?**", (route) => {
    lastQuery = route.request().url();
    return json(route, { items: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 0 });
  });

  await page.goto("/admin-v2/");
  await page.getByRole("link", { name: /Solicitudes de acceso pendientes/ }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Solicitudes de acceso" })).toBeVisible();
  await expect(page.getByRole("combobox", { name: "Estado", exact: true })).toHaveValue("Pending");
  await expect.poll(() => lastQuery).toContain("status=Pending");
});

test("searches users by keyboard in the access policy simulation", async ({ page }) => {
  const searches: string[] = [];
  await mockShell(page);
  await page.route(`**/api/applications/${applicationId}`, (route) => json(route, { id: applicationId, code: "TIENDITAPP", name: "TienditApp", description: null, isActive: true, createdAt: "2026-08-11T00:00:00Z", updatedAt: null, branding: null }));
  await page.route(`**/api/access-policies/applications/${applicationId}/versions`, (route) => json(route, [{ id: "v1", applicationSystemId: applicationId, versionNumber: 1, status: "Published", ruleCount: 0, createdAt: "2026-08-11T00:00:00Z", publishedAt: "2026-08-11T00:00:00Z" }]));
  await page.route(`**/api/access-policies/applications/${applicationId}?**`, (route) => json(route, []));
  await page.route("**/api/groups?**", (route) => json(route, { items: [], totalCount: 0, page: 1, pageSize: 100, totalPages: 0 }));
  await page.route("**/api/users?**", (route) => {
    const url = new URL(route.request().url());
    searches.push(url.searchParams.get("search") ?? "");
    expect(url.searchParams.get("applicationSystemId")).toBe(applicationId);
    return json(route, { items: [user(7), user(8)], totalCount: 2, page: 1, pageSize: 10, totalPages: 1 });
  });

  await page.goto(`/admin-v2/access-policies/${applicationId}`);
  const picker = page.getByRole("combobox", { name: "Usuario", exact: true });
  await picker.fill("usu");
  const options = page.getByRole("listbox", { name: "Usuarios para Usuario" }).getByRole("option");
  await expect(options).toHaveCount(2);
  await picker.press("ArrowDown");
  await expect(options.filter({ hasText: "Usuario 8" })).toHaveAttribute("aria-selected", "true");
  await picker.press("Enter");
  await expect(picker).toHaveValue("Usuario 8 · usuario8@example.test");
  await expect(page.getByRole("listbox", { name: "Usuarios para Usuario" })).toBeHidden();
  expect(searches).toContain("usu");
});
