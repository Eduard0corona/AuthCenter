import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { allPermissions, applicationId, json, mockShell, mockStepUp, paged } from "./support";

const hookId = "c0c0c0c0-c0c0-4c0c-8c0c-c0c0c0c0c0c0";
const deliveryId = "d1d1d1d1-d1d1-4d1d-8d1d-d1d1d1d1d1d1";
const eventId = "e2e2e2e2-e2e2-4e2e-8e2e-e2e2e2e2e2e2";
const catalog = [
  { type: "LOGIN_SUCCESS", category: "authentication" },
  { type: "LOGIN_FAILED", category: "authentication" },
  { type: "USER_CREATED", category: "users" },
  { type: "USER_DEACTIVATED", category: "users" }
];
const hook = {
  id: hookId, applicationSystemId: null, applicationName: null, name: "SIEM corporativo", url: "https://siem.example.test/authcenter",
  eventTypes: ["LOGIN_FAILED", "USER_CREATED"], isVerified: false, isActive: true, createdAt: "2026-09-20T10:00:00Z", verifiedAt: null as string | null, version: 3, previousSecretExpiresAt: null as string | null
};
const deadLetter = {
  id: deliveryId, eventId, eventType: "USER_CREATED", hookId, hookName: hook.name, status: "dead-letter", attemptCount: 8,
  createdAt: "2026-09-25T08:00:00Z", nextAttemptAt: "2026-09-25T09:00:00Z", deliveredAt: null, deadLetteredAt: "2026-09-25T09:00:00Z", lastError: "HTTP 503 Service Unavailable"
};
const auditEntry = {
  id: "a1a1a1a1-a1a1-4a1a-8a1a-a1a1a1a1a1a1", userId: "user-1", userEmail: "grace@example.test", userName: "Grace Hopper", applicationCode: "AUTHCENTER",
  action: "EVENT_HOOK_UPDATED", entityName: "EventHook", entityId: hookId, ipAddress: "203.0.113.7", userAgent: "Mozilla/5.0 (E2E)",
  metadataJson: "{\"result\":\"Success\",\"isVerified\":false}", traceId: "4bf92f3577b34da6a3ce929d0e0e4736", createdAt: "2026-09-26T11:30:00Z"
};

test("shows platform metrics that link to filtered pages, the environment and the version", async ({ page }) => {
  await mockShell(page);
  await page.route("**/api/admin-dashboard", (route) => json(route, {
    generatedAt: "2026-09-26T12:00:00Z", activeUsers: 1280, inactiveUsers: 42, activeApplications: 7, activeGroups: 18, pendingAccessRequests: 3, activeAccessReviews: 0, overdueAccessReviews: 0, pendingAccessReviewItems: 0, separationOfDutiesViolations: 0,
    activeFederationProviders: 2, expiringProvisioningTokens: 1, unverifiedEventHooks: 0, deadLetterDeliveries: 4, failedLoginsLast24Hours: 12, highRiskObservationsLast24Hours: 0
  }));
  await page.route("**/api/event-hooks/deliveries?**", (route) => json(route, paged([deadLetter])));
  await page.route("**/api/event-hooks?**", (route) => json(route, paged([hook], 1, 100)));

  await page.goto("/admin-v2/");
  await expect(page.getByRole("heading", { name: "Hola, Ada" })).toBeVisible();
  const status = page.getByRole("region", { name: "Estado de la plataforma" });
  await expect(status.getByRole("link", { name: /1,280\s*Usuarios activos/ })).toBeVisible();
  await expect(status.getByText("Inicios de sesión de riesgo alto")).toBeVisible();
  if (page.viewportSize()!.width > 760) {
    await expect(page.locator(".environment-pill")).toHaveText(/Producción/);
    await expect(page.locator(".sidebar__version")).toHaveText("AuthCenter v1.4.0 · 0123456");
  }

  await status.getByRole("link", { name: /4\s*Entregas en dead letter/ }).click();
  await expect(page).toHaveURL(/\/admin-v2\/event-hooks\/deliveries\?status=dead-letter$/);
  await expect(page.getByRole("button", { name: "Dead letters" })).toHaveAttribute("aria-pressed", "true");
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("hides the metrics from operators without the audit permission", async ({ page }) => {
  let dashboardRequests = 0;
  await mockShell(page, ["AUTHCENTER_USERS_READ"], "Staging");
  await page.route("**/api/admin-dashboard", (route) => { dashboardRequests += 1; return json(route, {}); });

  await page.goto("/admin-v2/");
  await expect(page.getByRole("heading", { name: "Hola, Ada" })).toBeVisible();
  await expect(page.getByRole("region", { name: "Estado de la plataforma" })).toHaveCount(0);
  expect(dashboardRequests).toBe(0);
});

test("filters the System Log with debounce, shows the actor and details, and exports CSV", async ({ page }) => {
  const requests: string[] = [];
  let exportUrl = "";
  await mockShell(page);
  await page.route("**/api/audit-logs?**", (route) => {
    requests.push(route.request().url());
    return json(route, paged([auditEntry]));
  });
  await page.route("**/api/audit-logs/export?**", async (route) => {
    exportUrl = route.request().url();
    await route.fulfill({ status: 200, contentType: "text/csv", headers: { "X-Total-Count": "1", "Content-Disposition": "attachment; filename=\"authcenter-system-log-test.csv\"" }, body: "id,createdAt,action\r\n" });
  });

  await page.goto("/admin-v2/system-log");
  await expect(page.getByRole("cell", { name: /Grace Hopper/ })).toBeVisible();
  const initialRequests = requests.length;
  await page.getByLabel("Acción").pressSequentially("event_hook_updated", { delay: 15 });
  await expect(page).toHaveURL(/action=EVENT_HOOK_UPDATED/);
  await expect.poll(() => requests.at(-1) ?? "").toContain("action=EVENT_HOOK_UPDATED");
  // Typing sends one request after the pause, not one per keystroke.
  expect(requests.length - initialRequests).toBeLessThanOrEqual(2);
  await expect(page.getByLabel("Acción")).toHaveValue("EVENT_HOOK_UPDATED");

  await page.getByRole("link", { name: "Event hook" }).first().isVisible();
  await page.getByRole("button", { name: /Detalle del evento EVENT_HOOK_UPDATED/ }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText("203.0.113.7");
  await expect(dialog).toContainText("Mozilla/5.0 (E2E)");
  await expect(dialog).toContainText("\"isVerified\": false");
  await expect(dialog).toContainText(auditEntry.traceId);
  expect((await new AxeBuilder({ page }).include("dialog").analyze()).violations).toEqual([]);
  await dialog.getByRole("button", { name: "Historial de esta entidad" }).click();
  await expect(page).toHaveURL(new RegExp(`entity=EventHook&entityId=${hookId}$`));
  await expect(page.getByLabel("ID de entidad")).toHaveValue(hookId);
  await expect.poll(() => requests.at(-1) ?? "").toContain(`entityId=${hookId}`);
  await expect(page.getByLabel("Acción")).toHaveValue("");

  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Exportar CSV" }).click();
  expect((await download).suggestedFilename()).toBe("authcenter-system-log-test.csv");
  expect(exportUrl).toContain("entityName=EventHook");
  await expect(page.getByRole("status").filter({ hasText: "Se exportaron 1 eventos." })).toBeVisible();
});

test("creates a hook from the event catalog and reveals its secret once", async ({ page }) => {
  let createPayload: Record<string, unknown> | null = null;
  await mockShell(page);
  await page.route("**/api/event-hooks/event-types", (route) => json(route, catalog));
  await page.route("**/api/applications?**", (route) => json(route, paged([{ id: applicationId, code: "TIENDITAPP", name: "TienditApp", description: null, isActive: true, createdAt: "2026-08-11T00:00:00Z", updatedAt: null, branding: null }], 1, 100)));
  await page.route("**/api/event-hooks", async (route) => {
    createPayload = route.request().postDataJSON() as Record<string, unknown>;
    await json(route, { id: hookId, secret: "whsec_e2e_revealed_once", isVerified: false, previousSecretExpiresAt: null }, 201);
  });
  await page.route(`**/api/event-hooks/${hookId}`, (route) => json(route, hook));

  await page.goto("/admin-v2/event-hooks/new");
  await page.getByLabel("Nombre").fill(hook.name);
  await page.getByLabel("URL del endpoint").fill("http://siem.example.test/authcenter");
  await page.getByRole("button", { name: "Crear hook" }).click();
  await expect(page.getByText(/Usa una URL HTTPS pública/)).toBeVisible();
  await expect(page.getByText("Selecciona al menos un tipo de evento.")).toBeVisible();

  await page.getByLabel("URL del endpoint").fill(hook.url);
  await page.getByLabel("Alcance").selectOption(applicationId);
  await page.getByLabel("Filtrar tipos").fill("user");
  await page.getByRole("checkbox", { name: "Todos los eventos de Usuarios" }).check();
  await page.getByLabel("Filtrar tipos").fill("");
  await page.getByRole("checkbox", { name: "LOGIN_FAILED" }).check();
  await expect(page.getByText("3 tipos seleccionados")).toBeVisible();
  await page.getByRole("button", { name: "Crear hook" }).click();

  const secretDialog = page.getByRole("dialog");
  await expect(secretDialog).toContainText("whsec_e2e_revealed_once");
  expect(createPayload).toEqual({ name: hook.name, url: hook.url, applicationSystemId: applicationId, eventTypes: ["USER_CREATED", "USER_DEACTIVATED", "LOGIN_FAILED"] });
  await secretDialog.getByRole("button", { name: "Ya guardé el secreto" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/event-hooks/${hookId}$`));
  await expect(page.getByRole("heading", { level: 1, name: hook.name })).toBeVisible();
  await expect(page.locator("main")).not.toContainText("whsec_e2e_revealed_once");
});

test("verifies the endpoint, rotates the secret with step-up and subscribes to every event", async ({ page }) => {
  const purposes: string[] = [];
  let rotateProof = "";
  let updatePayload: Record<string, unknown> | null = null;
  let current = { ...hook };
  await mockShell(page);
  await mockStepUp(page, (purpose) => purposes.push(purpose));
  await page.route("**/api/event-hooks/event-types", (route) => json(route, catalog));
  await page.route(`**/api/event-hooks/${hookId}`, async (route) => {
    if (route.request().method() === "PUT") {
      updatePayload = route.request().postDataJSON() as Record<string, unknown>;
      current = { ...current, eventTypes: ["*"], version: current.version + 1 };
    }
    await json(route, current);
  });
  await page.route(`**/api/event-hooks/${hookId}/verify`, async (route) => {
    current = { ...current, isVerified: true, verifiedAt: "2026-09-26T12:05:00Z", version: current.version + 1 };
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true }) });
  });
  await page.route(`**/api/event-hooks/${hookId}/rotate-secret`, async (route) => {
    rotateProof = route.request().headers()["x-authcenter-reauthentication"] ?? "";
    current = { ...current, previousSecretExpiresAt: "2026-09-27T12:10:00Z", version: current.version + 1 };
    await json(route, { id: hookId, secret: "whsec_rotated", isVerified: true, previousSecretExpiresAt: "2026-09-27T12:10:00Z" });
  });

  await page.goto(`/admin-v2/event-hooks/${hookId}`);
  await expect(page.getByText("Sin verificar", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Verificar endpoint" }).click();
  await expect(page.getByRole("status")).toContainText("Endpoint verificado");
  await expect(page.getByText(/^Verificado/)).toBeVisible();

  await page.getByRole("button", { name: "Rotar secreto" }).click();
  const stepUp = page.getByRole("dialog");
  await stepUp.getByLabel(/Tu contraseña actual/).fill("AdminSecret123");
  await stepUp.getByRole("button", { name: "Verificar y rotar" }).click();
  await expect(page.getByRole("dialog")).toContainText("whsec_rotated");
  await page.getByRole("dialog").getByRole("button", { name: "Ya guardé el secreto" }).click();
  await expect(page.getByText(/Rotación en curso/)).toBeVisible();
  expect(purposes).toEqual(["admin.event-hook.rotate-secret"]);
  expect(rotateProof).toBe("single-use-proof");

  await page.getByRole("checkbox", { name: /Todos los eventos, incluidos/ }).check();
  await page.getByRole("button", { name: "Guardar cambios" }).click();
  await expect(page.getByRole("status")).toContainText("Cambios guardados");
  expect(updatePayload).toEqual({ name: hook.name, url: hook.url, eventTypes: ["*"], isActive: true, version: 5 });
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("shows a stale hook update as a conflict and reloads the current version", async ({ page }) => {
  await mockShell(page);
  await page.route("**/api/event-hooks/event-types", (route) => json(route, catalog));
  await page.route(`**/api/event-hooks/${hookId}`, async (route) => {
    if (route.request().method() === "PUT") {
      await route.fulfill({ status: 409, contentType: "application/json", body: JSON.stringify({ success: false, errorCode: "CONCURRENCY_CONFLICT", message: "The event hook changed.", traceId: "trace-conflict" }) });
      return;
    }
    await json(route, hook);
  });

  await page.goto(`/admin-v2/event-hooks/${hookId}`);
  await page.getByLabel("Nombre").fill("SIEM renombrado");
  await page.getByRole("button", { name: "Guardar cambios" }).click();
  await expect(page.getByRole("alert")).toContainText("Alguien más cambió este recurso");
  await expect(page.getByRole("alert")).toContainText("Referencia: trace-conflict");
  await page.getByRole("button", { name: "Cargar la versión actual" }).click();
  await expect(page.getByLabel("Nombre")).toHaveValue(hook.name);
});

test("inspects a dead letter's payload and replays it once with an idempotency key", async ({ page }) => {
  const replayKeys: string[] = [];
  let lastQuery = "";
  await mockShell(page);
  await page.route("**/api/event-hooks?**", (route) => json(route, paged([hook], 1, 100)));
  await page.route("**/api/event-hooks/deliveries?**", (route) => {
    lastQuery = route.request().url();
    return json(route, paged([deadLetter]));
  });
  await page.route(`**/api/event-hooks/deliveries/${deliveryId}`, (route) => json(route, { ...deadLetter, payload: JSON.stringify({ id: eventId, type: "USER_CREATED", subjectId: "user-1" }) }));
  await page.route(`**/api/event-hooks/deliveries/${deliveryId}/replay`, async (route) => {
    replayKeys.push(route.request().headers()["idempotency-key"] ?? "");
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true }) });
  });

  await page.goto(`/admin-v2/event-hooks/deliveries?hook=${hookId}`);
  await expect.poll(() => lastQuery).toContain(`hookId=${hookId}`);
  await page.getByRole("button", { name: "Dead letters" }).click();
  await expect.poll(() => lastQuery).toContain("status=dead-letter");
  await expect(page.getByText("HTTP 503 Service Unavailable")).toBeVisible();

  await page.getByRole("button", { name: /Detalle de USER_CREATED/ }).click();
  const detail = page.getByRole("dialog", { name: /USER_CREATED/ });
  await expect(detail).toContainText("\"subjectId\": \"user-1\"");
  await detail.getByRole("button", { name: "Reintentar entrega" }).click();
  const confirm = page.getByRole("dialog", { name: "Reintentar dead letter" });
  await confirm.getByRole("button", { name: "Reintentar" }).click();
  await expect(page.getByRole("status")).toContainText(`El evento ${eventId} volvió a la cola.`);
  expect(replayKeys).toHaveLength(1);
  expect(replayKeys[0]).toMatch(/^[0-9a-f-]{36}$/);
});

test("operators without write access see hooks read-only", async ({ page }) => {
  await mockShell(page, allPermissions.filter((permission) => permission !== "AUTHCENTER_EVENT_HOOKS_WRITE"));
  await page.route("**/api/event-hooks?**", (route) => json(route, paged([hook])));
  await page.route("**/api/event-hooks/event-types", (route) => json(route, catalog));
  await page.route(`**/api/event-hooks/${hookId}`, (route) => json(route, hook));

  await page.goto("/admin-v2/event-hooks");
  await expect(page.getByRole("cell", { name: /SIEM corporativo https/ })).toBeVisible();
  await expect(page.getByRole("link", { name: "Nuevo hook" })).toHaveCount(0);
  await page.getByRole("link", { name: /Abrir SIEM corporativo/ }).click();
  await expect(page.getByLabel("Nombre")).toBeDisabled();
  await expect(page.getByRole("button", { name: "Rotar secreto" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Verificar endpoint" })).toHaveCount(0);
});
