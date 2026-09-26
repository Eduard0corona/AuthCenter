import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { json, mockShell, paged } from "./support";

// Every route of the console, with empty data, must be free of axe violations; the keyboard
// behaviour of the shell and of dialogs is checked below.

const routes = [
  "/", "/users", "/users/new", "/users/invite", "/groups", "/groups/new", "/profile-schema", "/profile-schema/new",
  "/applications", "/applications/new", "/oauth-clients", "/oauth-clients/new", "/api-resources", "/api-resources/new",
  "/provisioning-tokens", "/provisioning-tokens/new", "/profile-mappings", "/profile-mappings/new", "/group-rules", "/group-rules/new", "/saml-apps", "/saml-apps/new",
  "/federation", "/federation/providers/new", "/roles", "/roles/new", "/permissions", "/permissions/new", "/access-policies",
  "/event-hooks", "/event-hooks/new", "/event-hooks/deliveries", "/system-log",
  "/access-requests", "/access-reviews", "/access-reviews/new", "/sod-rules", "/sod-rules/new", "/404"
];

// Endpoints that answer a plain array instead of a page.
const arrayEndpoints = [/^\/api\/profile-schema$/, /^\/api\/event-hooks\/event-types$/, /^\/api\/federation\/providers$/, /^\/api\/federation\/routing-rules$/, /^\/api\/access-policies\/applications\//];

async function mockEmptyApi(page: Page) {
  // Only the API: the development server also serves source modules under /admin-v2/src/api/.
  await page.route((url) => url.pathname.startsWith("/api/"), (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/admin-dashboard") {
      return json(route, { generatedAt: "2026-09-26T12:00:00Z", activeUsers: 3, inactiveUsers: 0, activeApplications: 1, activeGroups: 0, pendingAccessRequests: 1, activeAccessReviews: 0, overdueAccessReviews: 0, pendingAccessReviewItems: 0, separationOfDutiesViolations: 0, activeFederationProviders: 0, expiringProvisioningTokens: 0, unverifiedEventHooks: 1, deadLetterDeliveries: 2, failedLoginsLast24Hours: 0, highRiskObservationsLast24Hours: 1 });
    }
    if (path === "/api/saml/identity-provider") return json(route, { isConfigured: true, problem: null, entityId: "https://authcenter.example.test/saml/idp/metadata", metadataUrl: "https://authcenter.example.test/saml/idp/metadata", singleSignOnUrl: "https://authcenter.example.test/saml/idp/sso", singleLogoutUrl: "https://authcenter.example.test/saml/idp/slo", certificate: null, nameIdFormats: [], attributeSources: [] });
    if (path === "/api/federation/service-provider") return json(route, { oidcCallbackUrl: "https://authcenter.example.test/api/federation/oidc/callback", samlEntityId: "https://authcenter.example.test/saml", samlAssertionConsumerServiceUrl: "https://authcenter.example.test/api/federation/saml/acs" });
    return json(route, arrayEndpoints.some((pattern) => pattern.test(path)) ? [] : paged([]));
  });
  await mockShell(page);
}

for (const route of routes) {
  test(`${route} has no axe violations`, async ({ page }, testInfo) => {
    test.skip(testInfo.project.name === "chromium-tablet", "Desktop and mobile layouts cover the tablet one.");
    await mockEmptyApi(page);
    await page.goto(`/admin-v2${route}`);
    await expect(page.locator("main h1").first()).toBeVisible();
    await expect(page.locator("[aria-busy='true']")).toHaveCount(0);
    const { violations } = await new AxeBuilder({ page }).analyze();
    expect(violations.map((violation) => `${violation.id}: ${violation.nodes.map((node) => node.target.join(" ")).join(", ")}`)).toEqual([]);
  });
}

test("the skip link moves focus to the content and navigation focuses each page's heading", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== "chromium-desktop", "Keyboard navigation of the desktop layout.");
  await mockEmptyApi(page);
  await page.goto("/admin-v2/");
  // Each page starts with focus on its heading, so screen readers announce it.
  await expect(page.getByRole("heading", { level: 1 })).toBeFocused();

  const skip = page.getByRole("link", { name: "Saltar al contenido" });
  await expect(skip).not.toBeInViewport();
  await skip.focus();
  await expect(skip).toBeInViewport();
  await page.keyboard.press("Enter");
  await expect(page.locator("#main-content")).toBeFocused();

  const systemLog = page.getByRole("link", { name: "System Log" }).first();
  await systemLog.focus();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/admin-v2\/system-log$/);
  await expect(page.getByRole("heading", { level: 1, name: "System Log" })).toBeFocused();
});

test("dialogs keep focus inside, close with Escape and give focus back to their trigger", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== "chromium-desktop", "Keyboard behaviour of dialogs.");
  await mockEmptyApi(page);
  const deadLetter = { id: "d1d1d1d1-d1d1-4d1d-8d1d-d1d1d1d1d1d1", eventId: "e2e2e2e2-e2e2-4e2e-8e2e-e2e2e2e2e2e2", eventType: "USER_CREATED", hookId: "c0c0c0c0-c0c0-4c0c-8c0c-c0c0c0c0c0c0", hookName: "SIEM", status: "dead-letter", attemptCount: 8, createdAt: "2026-09-25T08:00:00Z", nextAttemptAt: "2026-09-25T09:00:00Z", deliveredAt: null, deadLetteredAt: "2026-09-25T09:00:00Z", lastError: "HTTP 503" };
  await page.route("**/api/event-hooks/deliveries?**", (route) => json(route, paged([deadLetter])));

  await page.goto("/admin-v2/event-hooks/deliveries");
  const trigger = page.getByRole("button", { name: /Reintentar entrega/ });
  await trigger.focus();
  await page.keyboard.press("Enter");
  const dialog = page.getByRole("dialog", { name: "Reintentar dead letter" });
  await expect(dialog).toBeVisible();
  await expect.poll(() => dialog.evaluate((node) => node.contains(document.activeElement))).toBe(true);

  // Focus cycles among the dialog's own controls (the page behind is inert).
  for (let step = 0; step < 4; step += 1) {
    await page.keyboard.press("Tab");
    expect(await page.evaluate(() => document.activeElement?.closest("dialog") !== null || document.activeElement === document.body)).toBe(true);
  }

  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect(trigger).toBeFocused();
});

test("the mobile navigation drawer closes with Escape and returns focus to its button", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== "chromium-mobile", "The drawer only exists on small screens.");
  await mockEmptyApi(page);
  await page.goto("/admin-v2/");
  const menu = page.getByRole("button", { name: "Abrir navegación" });
  await menu.click();
  await expect(menu).toHaveAttribute("aria-expanded", "true");
  await page.keyboard.press("Escape");
  await expect(menu).toHaveAttribute("aria-expanded", "false");
  await expect(menu).toBeFocused();
});
