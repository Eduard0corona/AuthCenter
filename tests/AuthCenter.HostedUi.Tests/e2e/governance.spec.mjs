import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { adminToken, baseURL, call, createApplication, expectPortal, registerUser, signInWithPassword, statusOf } from "./support.mjs";

// Access governance in the portal: a user requests access to an application, its owner approves
// it from their own portal, and later reviews who keeps the access.

let token;

test.beforeAll(async ({ playwright }) => {
  const request = await playwright.request.newContext({ baseURL });
  token = await adminToken(request);
  await request.dispose();
});

test("a user requests access and the application's owner approves it", async ({ browser, page, request }) => {
  const home = await createApplication(request, token);
  const requested = await createApplication(request, token);
  const owner = await registerUser(request, home.code, { fullName: "Olga Responsable" });
  const requester = await registerUser(request, home.code, { fullName: "Rita Solicitante" });
  await setGovernance(request, requested.id, { accessRequestsEnabled: true, ownerUserIds: [await userId(request, owner.email)] });

  await signInWithPassword(page, requester, `/login?application=${home.code}&return_url=/portal`);
  await expectPortal(page, requester.fullName);
  await openPanel(page, "Aplicaciones");
  await page.getByLabel("Aplicación").selectOption({ label: `Aplicación ${requested.code}` });
  await page.getByLabel("Justificación").fill("Preparar los reportes del trimestre");
  await page.getByRole("button", { name: "Enviar solicitud" }).click();
  await expect(statusOf(page)).toContainText("Solicitud enviada");
  await expect(page.locator("#requests-list")).toContainText(`Aplicación ${requested.code}`);
  await expect(page.locator("#requests-list")).toContainText("Pendiente");
  expect((await new AxeBuilder({ page }).include("#applications").analyze()).violations).toEqual([]);

  const ownerContext = await browser.newContext();
  const ownerPage = await ownerContext.newPage();
  await signInWithPassword(ownerPage, owner, `/login?application=${home.code}&return_url=/portal`);
  await expectPortal(ownerPage, owner.fullName);
  await openPanel(ownerPage, "Aprobaciones");
  const pending = ownerPage.locator("#approval-requests-list .item", { hasText: requester.email });
  await expect(pending).toContainText("Preparar los reportes del trimestre");
  await pending.getByRole("button", { name: "Aprobar" }).click();
  const dialog = ownerPage.getByRole("dialog", { name: "Aprobar la solicitud" });
  await dialog.getByLabel("Comentario (opcional)").fill("Adelante");
  expect((await new AxeBuilder({ page: ownerPage }).include("#decision-dialog").analyze()).violations).toEqual([]);
  await dialog.getByRole("button", { name: "Aprobar" }).click();
  await expect(statusOf(ownerPage)).toContainText("Solicitud aprobada");
  await expect(ownerPage.locator("#approval-requests-list")).toContainText("No hay solicitudes esperando tu decisión.");
  await ownerContext.close();

  await page.reload();
  await expect(page.locator("#applications-list")).toContainText(`Aplicación ${requested.code}`);
  await expect(page.locator("#requests-list")).toContainText("Aprobada");
  await expect(page.locator("#requests-list")).toContainText("Adelante");
});

test("a rejection needs a reason, which the requester reads", async ({ browser, page, request }) => {
  const home = await createApplication(request, token);
  const requested = await createApplication(request, token);
  const owner = await registerUser(request, home.code, { fullName: "Omar Responsable" });
  const requester = await registerUser(request, home.code, { fullName: "Raúl Solicitante" });
  await setGovernance(request, requested.id, { accessRequestsEnabled: true, ownerUserIds: [await userId(request, owner.email)] });
  const requesterToken = await signInToken(request, requester, home.code);
  const created = await call(request, "POST", "/api/auth/access-requests", {
    token: requesterToken,
    data: { applicationSystemId: requested.id, justification: "Consultar facturas" }
  });
  expect(created.ok).toBe(true);

  const ownerContext = await browser.newContext();
  const ownerPage = await ownerContext.newPage();
  await signInWithPassword(ownerPage, owner, `/login?application=${home.code}&return_url=/portal`);
  await expectPortal(ownerPage, owner.fullName);
  await openPanel(ownerPage, "Aprobaciones");
  await ownerPage.locator("#approval-requests-list .item", { hasText: requester.email }).getByRole("button", { name: "Rechazar" }).click();
  const dialog = ownerPage.getByRole("dialog", { name: "Rechazar la solicitud" });
  await dialog.getByRole("button", { name: "Rechazar" }).click();
  await expect(dialog.getByRole("status")).toHaveText("Escribe el motivo.");
  await dialog.getByLabel("Motivo").fill("Pídelo a tu área de finanzas");
  await dialog.getByRole("button", { name: "Rechazar" }).click();
  await expect(statusOf(ownerPage)).toContainText("Solicitud rechazada");
  await ownerContext.close();

  await signInWithPassword(page, requester, `/login?application=${home.code}&return_url=/portal`);
  await expectPortal(page, requester.fullName);
  await openPanel(page, "Aplicaciones");
  await expect(page.locator("#requests-list")).toContainText("Rechazada");
  await expect(page.locator("#requests-list")).toContainText("Pídelo a tu área de finanzas");
});

test("an owner reviews who keeps access to the application", async ({ page, request }) => {
  const application = await createApplication(request, token);
  const owner = await registerUser(request, application.code, { fullName: "Oscar Revisor" });
  const member = await registerUser(request, application.code, { fullName: "Marta Miembro" });
  await setGovernance(request, application.id, { ownerUserIds: [await userId(request, owner.email)] });
  const created = await call(request, "POST", "/api/governance/access-reviews", {
    token,
    data: { name: "Revisión semestral", applicationSystemId: application.id, dueAt: new Date(Date.now() + 7 * 86_400_000).toISOString(), revokeUnreviewed: false }
  });
  expect(created.ok).toBe(true);

  await signInWithPassword(page, owner, `/login?application=${application.code}&return_url=/portal`);
  await expectPortal(page, owner.fullName);
  await openPanel(page, "Aprobaciones");
  const review = page.locator("#reviews-list .item", { hasText: "Revisión semestral" });
  await expect(review).toContainText("2 de 2 por revisar");
  await review.getByRole("button", { name: "Revisar" }).click();
  const items = page.locator("#review-items");
  await expect(items.locator(".item", { hasText: owner.email })).toContainText("tu propio acceso lo revisa otra persona");
  await expect(items.locator(".item", { hasText: owner.email }).getByRole("button")).toHaveCount(0);
  await items.locator(".item", { hasText: member.email }).getByRole("button", { name: "Revocar" }).click();
  await page.getByRole("dialog", { name: "Revocar el acceso" }).getByRole("button", { name: "Revocar" }).click();
  await expect(statusOf(page)).toContainText("Acceso revocado.");
  await expect(items.locator(".item", { hasText: member.email })).toContainText("Revocado");
  await expect(review).toContainText("1 de 2 por revisar");
  expect((await new AxeBuilder({ page }).include("#approvals").analyze()).violations).toEqual([]);

  // Without access, the member can no longer sign in to the application.
  const denied = await call(request, "POST", "/api/auth/login", { data: { email: member.email, password: member.password, applicationCode: application.code } });
  expect(denied.ok).toBe(false);
});

async function openPanel(page, name) {
  await page.getByRole("navigation", { name: "Mi cuenta" }).getByRole("link", { name, exact: true }).click();
}

async function setGovernance(request, applicationId, settings) {
  const current = await call(request, "GET", `/api/governance/applications/${applicationId}`, { token });
  const result = await call(request, "PUT", `/api/governance/applications/${applicationId}`, {
    token,
    data: { accessRequestsEnabled: false, ownerUserIds: [], ...settings, version: current.data.version }
  });
  if (!result.ok) throw new Error(`Governance settings were not saved: ${result.status} ${JSON.stringify(result.body)}`);
}

async function userId(request, email) {
  const result = await call(request, "GET", `/api/users?search=${encodeURIComponent(email)}`, { token });
  return result.data.items.find(user => user.email === email).id;
}

async function signInToken(request, user, applicationCode) {
  const result = await call(request, "POST", "/api/auth/login", { data: { email: user.email, password: user.password, applicationCode } });
  if (!result.ok) throw new Error(`Sign-in failed: ${result.status} ${JSON.stringify(result.body)}`);
  return result.data.accessToken;
}
