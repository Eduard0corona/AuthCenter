import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { applicationId, json, mockShell, paged } from "./support";

const application = { id: applicationId, code: "ERP", name: "ERP corporativo", description: null, isActive: true, createdAt: "2026-08-11T00:00:00Z", updatedAt: null, branding: null, version: 2 };
const payer = { id: "33333333-3333-4333-8333-333333333333", name: "Pagador", description: null, applicationSystemId: applicationId, isSystemRole: false, isActive: true, createdAt: "2026-08-11T00:00:00Z", permissions: [] };
const approver = { ...payer, id: "44444444-4444-4444-8444-444444444444", name: "Aprobador" };
const rita = { id: "55555555-5555-4555-8555-555555555555", fullName: "Rita Solicitante", email: "rita@example.test", isActive: true };

function request(overrides: Record<string, unknown> = {}) {
  return {
    id: "66666666-6666-4666-8666-666666666666", requester: rita, applicationSystemId: applicationId, applicationCode: "ERP", applicationName: "ERP corporativo",
    roleId: payer.id, roleName: "Pagador", source: "Portal", status: "Pending", justification: "Cierre contable", createdAt: "2026-09-26T10:00:00Z",
    expiresAt: "2026-10-26T10:00:00Z", decidedAt: null, decidedBy: null, decisionComment: null, version: 0, ...overrides
  };
}

test.beforeEach(async ({ page }) => {
  await mockShell(page);
  await page.route("**/api/applications?**", (route) => json(route, paged([application], 1, 100)));
});

test("decides the access requests waiting for approval", async ({ page }) => {
  const decisions: Array<{ url: string; body: unknown }> = [];
  const own = request({ id: "77777777-7777-4777-8777-777777777777", requester: { id: "operator-1", fullName: "Ada Operadora", email: "ada@example.test", isActive: true } });
  await page.route("**/api/governance/access-requests?**", (route) => json(route, paged([request(), own])));
  await page.route("**/api/governance/access-requests/*/approve", async (route) => {
    decisions.push({ url: route.request().url(), body: route.request().postDataJSON() });
    await json(route, request({ status: "Approved", decidedBy: { id: "operator-1", fullName: "Ada Operadora", email: "ada@example.test", isActive: true }, decisionComment: "Adelante" }));
  });
  await page.route("**/api/governance/access-requests/*/reject", async (route) => {
    decisions.push({ url: route.request().url(), body: route.request().postDataJSON() });
    await json(route, request({ status: "Rejected", decisionComment: "No aplica" }));
  });

  await page.goto("/admin-v2/access-requests");
  await expect(page.getByRole("heading", { level: 1, name: "Solicitudes de acceso" })).toBeVisible();
  const row = page.getByRole("row", { name: /Rita Solicitante/ });
  await expect(row).toContainText("Cierre contable");
  // Nobody decides their own request: the operator's own row has no actions.
  await expect(page.getByRole("row", { name: /Ada Operadora/ }).getByRole("button")).toHaveCount(0);

  await row.getByRole("button", { name: /Rechazar/ }).click();
  const reject = page.getByRole("dialog", { name: "Rechazar la solicitud" });
  await reject.getByRole("button", { name: "Rechazar" }).click();
  await expect(reject.getByText("Escribe el motivo: la persona lo leerá.")).toBeVisible();
  expect(decisions).toHaveLength(0);
  await reject.getByLabel("Motivo").fill("No aplica");
  await reject.getByRole("button", { name: "Rechazar" }).click();
  await expect(page.getByRole("status").filter({ hasText: "solicitud rechazada" })).toBeVisible();

  await row.getByRole("button", { name: /Aprobar/ }).click();
  const approve = page.getByRole("dialog", { name: "Aprobar la solicitud" });
  await approve.getByLabel("Comentario (opcional)").fill("Adelante");
  expect((await new AxeBuilder({ page }).include("dialog[open]").analyze()).violations).toEqual([]);
  await approve.getByRole("button", { name: "Aprobar" }).click();
  await expect(page.getByRole("status").filter({ hasText: "solicitud aprobada" })).toBeVisible();
  expect(decisions.map((decision) => decision.body)).toEqual([{ comment: "No aplica" }, { comment: "Adelante" }]);
});

test("explains a separation of duties conflict when approving", async ({ page }) => {
  await page.route("**/api/governance/access-requests?**", (route) => json(route, paged([request()])));
  await page.route("**/api/governance/access-requests/*/approve", (route) => json(route, {
    success: false, errorCode: "SOD_CONFLICT", message: "rita@example.test would hold both…", details: ["rita@example.test", "ERP:Pagador", "ERP:Aprobador", "Pagos"]
  }, 409));

  await page.goto("/admin-v2/access-requests");
  await page.getByRole("row", { name: /Rita Solicitante/ }).getByRole("button", { name: /Aprobar/ }).click();
  const dialog = page.getByRole("dialog", { name: "Aprobar la solicitud" });
  await dialog.getByRole("button", { name: "Aprobar" }).click();
  await expect(dialog.getByRole("alert")).toHaveText("rita@example.test tendría a la vez los roles «ERP:Pagador» y «ERP:Aprobador», que la regla de segregación de funciones «Pagos» no permite.");
});

test("starts an access review and decides its items", async ({ page }) => {
  const reviewId = "88888888-8888-4888-8888-888888888888";
  let created: Record<string, unknown> | null = null;
  const decisions: unknown[] = [];
  const review = {
    id: reviewId, name: "Revisión trimestral", applicationSystemId: applicationId, applicationCode: "ERP", applicationName: "ERP corporativo", status: "Active",
    createdAt: "2026-09-26T10:00:00Z", dueAt: "2026-10-10T10:00:00Z", completedAt: null, revokeUnreviewed: true, recurrenceMonths: 3, previousCampaignId: null,
    totalItems: 2, pendingItems: 2, keptItems: 0, revokedItems: 0, remediationItems: 0, reviewers: [{ id: "o1", fullName: "Olga Responsable", email: "olga@example.test", isActive: true }], version: 1
  };
  const item = (overrides: Record<string, unknown>) => ({
    id: "i1", campaignId: reviewId, user: rita, hasDirectAccess: true, groups: [], roles: ["Pagador"], decision: "Pending", decidedAt: null, decidedBy: null,
    decidedAutomatically: false, comment: null, outcome: null, remediationRequired: false, canDecide: true, version: 0, ...overrides
  });
  await page.route("**/api/governance/access-reviews", async (route) => {
    created = route.request().postDataJSON() as Record<string, unknown>;
    await json(route, review, 201);
  });
  await page.route(`**/api/governance/access-reviews/${reviewId}`, (route) => json(route, review));
  await page.route(`**/api/governance/access-reviews/${reviewId}/items?**`, (route) => json(route, paged([
    item({}),
    item({ id: "i2", user: { id: "u2", fullName: "Gonzalo Grupo", email: "gonzalo@example.test", isActive: true }, hasDirectAccess: false, groups: ["Tesorería"] })
  ])));
  await page.route(`**/api/governance/access-reviews/${reviewId}/items/*/decision`, async (route) => {
    decisions.push(route.request().postDataJSON());
    await json(route, item({ id: "i2", decision: "Revoke", remediationRequired: true, outcome: "Still granted through groups: Tesorería.", canDecide: false }));
  });

  await page.goto("/admin-v2/access-reviews/new");
  await page.getByLabel("Nombre").fill("Revisión trimestral");
  await page.getByLabel("Aplicación").selectOption(applicationId);
  await page.getByLabel("Repetir").selectOption("3");
  await page.getByLabel(/Revocar los accesos que nadie revise/).check();
  await page.getByRole("button", { name: "Iniciar revisión" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/access-reviews/${reviewId}$`));
  expect(created).toMatchObject({ name: "Revisión trimestral", applicationSystemId: applicationId, revokeUnreviewed: true, recurrenceMonths: 3 });

  await expect(page.getByRole("heading", { level: 1, name: "Revisión trimestral" })).toBeVisible();
  const grouped = page.getByRole("row", { name: /Gonzalo Grupo/ });
  await expect(grouped).toContainText("Tesorería");
  await grouped.getByRole("button", { name: /Revocar/ }).click();
  const dialog = page.getByRole("dialog", { name: "Revocar el acceso" });
  await expect(dialog).toContainText("Tesorería");
  await dialog.getByRole("button", { name: "Revocar" }).click();
  await expect(page.getByRole("status").filter({ hasText: "sus grupos todavía le dan acceso" })).toBeVisible();
  expect(decisions).toEqual([{ decision: "Revoke", comment: null }]);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("creates a separation of duties rule and lists who breaks one", async ({ page }) => {
  const ruleId = "99999999-9999-4999-8999-999999999999";
  let posted: Record<string, unknown> | null = null;
  const rule = {
    id: ruleId, name: "Pagos", description: "Quien paga no aprueba", isActive: true, violationCount: 1, createdAt: "2026-09-26T10:00:00Z", updatedAt: null, version: 0,
    firstRole: { id: payer.id, name: "Pagador", applicationSystemId: applicationId, applicationCode: "ERP", isActive: true },
    secondRole: { id: approver.id, name: "Aprobador", applicationSystemId: applicationId, applicationCode: "ERP", isActive: true }
  };
  await page.route("**/api/roles?**", (route) => json(route, paged([payer, approver], 1, 100)));
  await page.route("**/api/governance/sod-rules?**", (route) => json(route, paged([rule])));
  await page.route("**/api/governance/sod-violations?**", (route) => json(route, paged([{
    ruleId, ruleName: "Pagos", user: rita,
    firstRole: { roleId: payer.id, roleName: "Pagador", applicationCode: "ERP", direct: true, groups: [] },
    secondRole: { roleId: approver.id, roleName: "Aprobador", applicationCode: "ERP", direct: false, groups: ["Tesorería"] }
  }])));
  let attempts = 0;
  await page.route("**/api/governance/sod-rules", async (route) => {
    posted = route.request().postDataJSON() as Record<string, unknown>;
    attempts += 1;
    if (attempts === 1) {
      await json(route, { success: false, errorCode: "SOD_RULE_EXISTS", message: "Another rule already keeps these two roles apart." }, 409);
      return;
    }
    await json(route, rule, 201);
  });
  await page.route(`**/api/governance/sod-rules/${ruleId}`, (route) => json(route, rule));

  await page.goto("/admin-v2/sod-rules");
  await expect(page.getByRole("row", { name: /Pagos/ }).first()).toContainText("ERP · Pagador");
  const violations = page.getByRole("region", { name: "Violaciones actuales" });
  await expect(violations).toContainText("Rita Solicitante");
  await expect(violations).toContainText("ERP · Aprobador (grupos: Tesorería)");

  await page.goto("/admin-v2/sod-rules/new");
  await page.getByLabel("Nombre").fill("Pagos");
  await page.getByLabel("Primer rol").selectOption(payer.id);
  await page.getByLabel("Segundo rol").selectOption(payer.id);
  await page.getByRole("button", { name: "Crear regla" }).click();
  await expect(page.getByText("Elige dos roles distintos.")).toBeVisible();
  await page.getByLabel("Segundo rol").selectOption(approver.id);
  await page.getByRole("button", { name: "Crear regla" }).click();
  await expect(page.getByRole("alert")).toContainText("Ya existe una regla con ese nombre o para esos dos roles.");
  await page.getByRole("button", { name: "Crear regla" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/sod-rules/${ruleId}$`));
  expect(posted).toMatchObject({ name: "Pagos", firstRoleId: payer.id, secondRoleId: approver.id, isActive: true });
});

test("names the owners of an application and opens it to requests", async ({ page }) => {
  let saved: Record<string, unknown> | null = null;
  await page.route(`**/api/applications/${applicationId}`, (route) => json(route, {
    ...application,
    registrationSettings: { registrationMode: "Open", audience: "Consumers", allowGoogleLogin: false, allowMicrosoftLogin: false, allowGitHubLogin: false, allowAppleLogin: false, allowMagicLink: false, allowPasswordLogin: true, requireEmailConfirmation: false, requireMfa: false, allowedEmailDomains: null, defaultRoleId: null }
  }));
  await page.route("**/api/roles?**", (route) => json(route, paged([payer], 1, 100)));
  await page.route(`**/api/governance/applications/${applicationId}`, async (route) => {
    if (route.request().method() === "PUT") {
      saved = route.request().postDataJSON() as Record<string, unknown>;
      await json(route, { applicationSystemId: applicationId, applicationCode: "ERP", applicationName: "ERP corporativo", accessRequestsEnabled: true, owners: [rita], version: 1 });
      return;
    }
    await json(route, { applicationSystemId: applicationId, applicationCode: "ERP", applicationName: "ERP corporativo", accessRequestsEnabled: false, owners: [], version: 0 });
  });
  await page.route("**/api/users?**", (route) => json(route, paged([{ ...rita, isExternalUser: false, mfaEnabled: false, roles: [], applications: [], createdAt: "2026-08-11T00:00:00Z", lastLoginAt: null }])));

  await page.goto(`/admin-v2/applications/${applicationId}`);
  const panel = page.getByRole("region", { name: "Responsables y solicitudes de acceso" });
  await expect(panel).toContainText("Sin responsables");
  await panel.getByRole("combobox", { name: "Agregar responsable" }).fill("Rita");
  await panel.getByRole("option", { name: /Rita Solicitante/ }).click();
  await panel.getByRole("button", { name: "Agregar" }).click();
  await panel.getByLabel("Los usuarios pueden solicitar acceso desde su portal").check();
  await panel.getByRole("button", { name: "Guardar responsables" }).click();
  await expect(panel.getByRole("status")).toHaveText("Responsables y solicitudes guardados.");
  expect(saved).toEqual({ accessRequestsEnabled: true, ownerUserIds: [rita.id], version: 0 });
  await expect(panel.getByRole("list", { name: "Responsables" })).toContainText("Rita Solicitante");
});
