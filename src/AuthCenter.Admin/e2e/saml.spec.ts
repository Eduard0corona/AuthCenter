import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { applicationId, json, mockShell, paged } from "./support";

const application = { id: applicationId, code: "CRM", name: "CRM corporativo", description: null, isActive: true, createdAt: "2026-08-11T00:00:00Z", updatedAt: null, branding: null };
const certificate = { pem: "-----BEGIN CERTIFICATE-----\nMIIC\n-----END CERTIFICATE-----", subject: "CN=AuthCenter SAML", thumbprintSha256: "AB12CD34", notBefore: "2026-01-01T00:00:00Z", notAfter: "2027-01-01T00:00:00Z" };
const identityProvider = {
  isConfigured: true, problem: null, entityId: "https://id.example.test/saml/idp/metadata", metadataUrl: "https://id.example.test/saml/idp/metadata",
  singleSignOnUrl: "https://id.example.test/saml/idp/sso", singleLogoutUrl: "https://id.example.test/saml/idp/slo", certificate,
  nameIdFormats: [], attributeSources: []
};
const providerId = "5a5a5a5a-5a5a-4a5a-8a5a-5a5a5a5a5a5a";
const provider = {
  version: 1, id: providerId, applicationSystemId: applicationId, applicationCode: "CRM", applicationName: "CRM corporativo", name: "CRM", entityId: "https://crm.example.test/saml",
  assertionConsumerServiceUrls: ["https://crm.example.test/saml/acs"], singleLogoutServiceUrl: "https://crm.example.test/saml/slo",
  nameIdFormat: "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent", signingCertificate: { ...certificate, subject: "CN=CRM" }, requireSignedRequests: true,
  encryptionCertificate: null, encryptAssertions: false, signResponse: true, attributes: [{ name: "email", source: "email" }, { name: "displayName", source: "name" }],
  allowIdpInitiated: true, defaultRelayState: "/inicio", launchUrl: `/saml/idp/sso/initiate/${providerId}`, assertionLifetimeMinutes: 5, isActive: true,
  createdAt: "2026-09-26T00:00:00Z", updatedAt: null
};

test.beforeEach(async ({ page }) => {
  await mockShell(page);
  await page.route("**/api/saml/identity-provider", (route) => json(route, identityProvider));
  await page.route("**/api/applications?**", (route) => json(route, paged([application])));
  await page.route("**/api/profile-schema", (route) => json(route, []));
});

test("registers a SAML application from its metadata", async ({ page }) => {
  let payload: Record<string, unknown> | null = null;
  let metadataXml = "";
  await page.route("**/api/saml/service-providers/parse-metadata", async (route) => {
    metadataXml = (route.request().postDataJSON() as { metadataXml: string }).metadataXml;
    await json(route, {
      entityId: provider.entityId, assertionConsumerServiceUrls: provider.assertionConsumerServiceUrls, singleLogoutServiceUrl: provider.singleLogoutServiceUrl,
      nameIdFormat: provider.nameIdFormat, signingCertificate: certificate.pem, encryptionCertificate: null, requireSignedRequests: true,
      warnings: ["Only HTTP-POST assertion consumer services are used; the others were left out."]
    });
  });
  await page.route("**/api/saml/service-providers", async (route) => {
    payload = route.request().postDataJSON() as Record<string, unknown>;
    await json(route, provider, 201);
  });
  await page.route(`**/api/saml/service-providers/${providerId}`, (route) => json(route, provider));

  await page.goto("/admin-v2/saml-apps/new");
  await expect(page.getByRole("heading", { level: 1, name: "Nueva aplicación SAML" })).toBeVisible();
  await page.getByLabel("Metadatos XML").fill("<md:EntityDescriptor entityID=\"https://crm.example.test/saml\"/>");
  await page.getByRole("button", { name: "Leer metadatos" }).click();
  await expect(page.getByRole("status")).toContainText("Los metadatos llenaron el formulario");
  await expect(page.getByLabel("Entity ID")).toHaveValue(provider.entityId);
  await expect(page.getByLabel("Exigir que las solicitudes vengan firmadas")).toBeChecked();
  await expect(page.getByText("Only HTTP-POST assertion consumer services are used")).toBeVisible();
  expect(metadataXml).toContain("EntityDescriptor");

  await page.getByLabel("Aplicación de AuthCenter").selectOption(applicationId);
  await page.getByLabel("Nombre", { exact: true }).fill("CRM");
  await page.getByLabel("Permitir el inicio desde AuthCenter").check();
  await page.getByRole("button", { name: "Agregar atributo" }).click();
  await page.getByLabel("Nombre del atributo 3").fill("roles");
  await page.getByLabel("Origen del atributo 3").selectOption("roles");
  await page.getByRole("button", { name: "Crear aplicación SAML" }).click();

  await expect(page).toHaveURL(new RegExp(`/admin-v2/saml-apps/${providerId}$`));
  expect(payload).toMatchObject({
    applicationSystemId: applicationId, name: "CRM", entityId: provider.entityId, assertionConsumerServiceUrls: provider.assertionConsumerServiceUrls,
    singleLogoutServiceUrl: provider.singleLogoutServiceUrl, nameIdFormat: provider.nameIdFormat, requireSignedRequests: true, allowIdpInitiated: true,
    attributes: [{ name: "email", source: "email" }, { name: "displayName", source: "name" }, { name: "roles", source: "roles" }]
  });
  await expect(page.getByText("CN=CRM")).toBeVisible();
  await expect(page.getByLabel("Enlace de inicio")).toHaveValue(new RegExp(`/saml/idp/sso/initiate/${providerId}$`));
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("lists SAML applications next to the data they register about AuthCenter", async ({ page }) => {
  const requests: string[] = [];
  await page.route("**/api/saml/service-providers?**", async (route) => {
    requests.push(route.request().url());
    await json(route, paged([provider]));
  });

  await page.goto("/admin-v2/saml-apps");
  await expect(page.getByRole("heading", { level: 1, name: "Aplicaciones SAML", exact: true })).toBeVisible();
  const idp = page.getByRole("region", { name: "AuthCenter como proveedor de identidad" });
  await expect(idp).toContainText(identityProvider.metadataUrl);
  await expect(idp).toContainText(identityProvider.singleSignOnUrl);
  await expect(idp).toContainText("AB12CD34");
  await expect(page.getByRole("row", { name: /CRM https:\/\/crm\.example\.test\/saml/ })).toBeVisible();
  await page.getByLabel("Estado").selectOption("true");
  await expect.poll(() => requests.at(-1)).toContain("isActive=true");
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("warns when AuthCenter cannot sign assertions", async ({ page }) => {
  await page.unroute("**/api/saml/identity-provider");
  await page.route("**/api/saml/identity-provider", (route) => json(route, { ...identityProvider, isConfigured: false, certificate: null, problem: "Configure Saml:SigningCertificateBase64." }));
  await page.route("**/api/saml/service-providers?**", (route) => json(route, paged([])));
  await page.goto("/admin-v2/saml-apps");
  await expect(page.getByRole("alert")).toContainText("Configure Saml:SigningCertificateBase64.");
  await expect(page.getByText("Sin configurar")).toBeVisible();
});

test("offers the current version when the SAML application changed meanwhile", async ({ page }) => {
  let updates = 0;
  await page.route(`**/api/saml/service-providers/${providerId}`, async (route) => {
    if (route.request().method() === "PUT") {
      updates += 1;
      await json(route, { success: false, errorCode: "CONCURRENCY_CONFLICT", message: "The SAML application changed after it was loaded." }, 409);
      return;
    }
    await json(route, provider);
  });
  await page.goto(`/admin-v2/saml-apps/${providerId}`);
  await page.getByLabel("Nombre", { exact: true }).fill("CRM renombrado");
  await page.getByRole("button", { name: "Guardar cambios" }).click();
  await expect(page.getByRole("alert")).toContainText("Alguien más cambió este registro");
  await page.getByRole("button", { name: "Cargar la versión actual" }).click();
  await expect(page.getByLabel("Nombre", { exact: true })).toHaveValue("CRM");
  expect(updates).toBe(1);
});
