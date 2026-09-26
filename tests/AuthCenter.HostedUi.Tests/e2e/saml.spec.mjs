import { randomBytes } from "node:crypto";
import { deflateRawSync } from "node:zlib";
import { expect, test } from "@playwright/test";
import { adminToken, baseURL, call, createApplication, expectPortal, registerUser, signInWithPassword } from "./support.mjs";

// AuthCenter as the SAML identity provider of an application: its sign-in request continues
// through the hosted login, and the signed assertion is posted to the application's assertion
// consumer service, which the browser reaches through a route of the test (there is no real one).

let application;
let serviceProvider;
let idpCertificate;

test.beforeAll(async ({ playwright }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await adminToken(request);
  application = await createApplication(request, token);
  const host = `sp-${randomBytes(4).toString("hex")}.e2e.test`;
  const created = await call(request, "POST", "/api/saml/service-providers", {
    token,
    data: {
      applicationSystemId: application.id,
      name: "Aplicación SAML de prueba",
      entityId: `https://${host}/saml`,
      assertionConsumerServiceUrls: [`https://${host}/saml/acs`],
      nameIdFormat: "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress",
      attributes: [{ name: "email", source: "email" }, { name: "displayName", source: "name" }],
      allowIdpInitiated: true,
      defaultRelayState: "/inicio"
    }
  });
  if (!created.ok) throw new Error(`The SAML application was not created: ${created.status} ${JSON.stringify(created.body)}`);
  serviceProvider = created.data;
  const metadata = await (await request.get("/saml/idp/metadata")).text();
  idpCertificate = certificateIn(metadata);
  await request.dispose();
});

test("a SAML sign-in request continues through the hosted login and returns a signed assertion", async ({ page, request }) => {
  const user = await registerUser(request, application.code);
  const acs = await catchPosts(page, serviceProvider.assertionConsumerServiceUrls[0]);
  const requestId = `_${randomBytes(16).toString("hex")}`;

  await page.goto(`/saml/idp/sso?${new URLSearchParams({ SAMLRequest: authnRequest(requestId), RelayState: "carrito-42" })}`);

  await expect(page).toHaveURL(/\/login\?saml_interaction=/);
  await expect(page.locator("#brand-name")).toHaveText(`Aplicación ${application.code}`);
  await page.locator("#email").fill(user.email);
  await page.locator("#password").fill(user.password);
  await page.getByRole("button", { name: "Continuar", exact: true }).click();

  const posted = await acs.next();
  expect(posted.get("RelayState")).toBe("carrito-42");
  const response = decode(posted.get("SAMLResponse"));
  expect(response).toContain("urn:oasis:names:tc:SAML:2.0:status:Success");
  expect(response).toContain(`InResponseTo="${requestId}"`);
  expect(textOf(response, "Audience")).toBe(serviceProvider.entityId);
  expect(textOf(response, "NameID")).toBe(user.email);
  expect(response).toMatch(/<(\w+:)?Signature\b/);
  // Signed with the certificate AuthCenter publishes in its metadata.
  expect(certificateIn(response)).toBe(idpCertificate);
  await expect(page.getByRole("heading", { name: "Aplicación SAML" })).toBeVisible();
});

test("the portal opens the SAML application, and its next request needs no sign-in", async ({ page, request }) => {
  const user = await registerUser(request, application.code);
  const acs = await catchPosts(page, serviceProvider.assertionConsumerServiceUrls[0]);
  await signInWithPassword(page, user, `/login?application=${application.code}&return_url=/portal`);
  await expectPortal(page);

  // Started by AuthCenter: an unsolicited response with the application's default RelayState.
  await page.getByRole("navigation", { name: "Mi cuenta" }).getByRole("link", { name: "Aplicaciones" }).click();
  await page.getByRole("link", { name: `Abrir Aplicación ${application.code}` }).click();
  const launched = await acs.next();
  expect(launched.get("RelayState")).toBe("/inicio");
  const unsolicited = decode(launched.get("SAMLResponse"));
  expect(unsolicited).toContain("urn:oasis:names:tc:SAML:2.0:status:Success");
  expect(unsolicited).not.toContain("InResponseTo=");
  expect(textOf(unsolicited, "NameID")).toBe(user.email);

  // The browser session answers the application's own request without the login.
  const requestId = `_${randomBytes(16).toString("hex")}`;
  await page.goto(`/saml/idp/sso?${new URLSearchParams({ SAMLRequest: authnRequest(requestId) })}`);
  const answered = decode((await acs.next()).get("SAMLResponse"));
  expect(answered).toContain(`InResponseTo="${requestId}"`);
  expect(textOf(answered, "NameID")).toBe(user.email);
});

test("a request from an application AuthCenter does not know is refused on AuthCenter's page", async ({ page }) => {
  const posts = [];
  await page.route("https://unknown.e2e.test/**", route => { posts.push(route.request().url()); return route.abort(); });
  const unknown = authnRequest(`_${randomBytes(16).toString("hex")}`, { issuer: "https://unknown.e2e.test/saml", acs: "https://unknown.e2e.test/saml/acs" });

  const response = await page.goto(`/saml/idp/sso?${new URLSearchParams({ SAMLRequest: unknown })}`);

  expect(response.status()).toBe(400);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("No se pudo iniciar sesión en la aplicación");
  await expect(page.getByRole("alert")).toHaveText("The application that sent this request is not registered in AuthCenter.");
  expect(posts).toEqual([]);
});

/** An unsigned AuthnRequest (HTTP-Redirect binding: raw DEFLATE, then base64) of the test application. */
function authnRequest(id, { issuer = serviceProvider.entityId, acs = serviceProvider.assertionConsumerServiceUrls[0] } = {}) {
  const xml = `<samlp:AuthnRequest xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion"`
    + ` ID="${id}" Version="2.0" IssueInstant="${new Date().toISOString().replace(/\.\d{3}Z$/, "Z")}" Destination="${baseURL}/saml/idp/sso"`
    + ` AssertionConsumerServiceURL="${acs}" ProtocolBinding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST">`
    + `<saml:Issuer>${issuer}</saml:Issuer></samlp:AuthnRequest>`;
  return deflateRawSync(Buffer.from(xml, "utf8")).toString("base64");
}

/** The form posts that reach the assertion consumer service, in order; each gets a page back. */
async function catchPosts(page, url) {
  const posts = [];
  const waiting = [];
  await page.route(url, async route => {
    const fields = new URLSearchParams(route.request().postData() ?? "");
    const resolve = waiting.shift();
    if (resolve) resolve(fields); else posts.push(fields);
    await route.fulfill({ status: 200, contentType: "text/html; charset=utf-8", body: "<!doctype html><html lang=\"es\"><title>SP</title><h1>Aplicación SAML</h1></html>" });
  });
  return {
    next: () => posts.length
      ? Promise.resolve(posts.shift())
      : new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error(`Nothing was posted to ${url}.`)), 20_000);
        waiting.push(fields => { clearTimeout(timer); resolve(fields); });
      })
  };
}

function decode(value) {
  expect(value, "a SAMLResponse field").toBeTruthy();
  return Buffer.from(value, "base64").toString("utf8");
}

function textOf(xml, localName) {
  return new RegExp(`<(?:\\w+:)?${localName}\\b[^>]*>([^<]*)<`).exec(xml)?.[1].trim();
}

function certificateIn(xml) {
  return textOf(xml, "X509Certificate")?.replace(/\s+/g, "");
}
