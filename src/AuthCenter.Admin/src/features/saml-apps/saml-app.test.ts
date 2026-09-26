import { describe, expect, it } from "vitest";
import type { SamlServiceProvider } from "../../api/types";
import { applyMetadata, createSamlAppPayload, describeSource, isBrowserEndpoint, newSamlAppSchema, samlAppDefaults, samlAppSchema, updateSamlAppPayload } from "./saml-app";

const provider: SamlServiceProvider = {
  version: 3, id: "p1", applicationSystemId: "11111111-1111-4111-8111-111111111111", applicationCode: "CRM", applicationName: "CRM",
  name: "CRM", entityId: "https://crm.example.test/saml", assertionConsumerServiceUrls: ["https://crm.example.test/acs", "https://crm.example.test/acs2"],
  singleLogoutServiceUrl: "https://crm.example.test/slo", nameIdFormat: "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent",
  signingCertificate: null, requireSignedRequests: false, encryptionCertificate: null, encryptAssertions: false, signResponse: true,
  attributes: [{ name: "mail", source: "email" }], allowIdpInitiated: true, defaultRelayState: "/home", launchUrl: "/saml/idp/sso/initiate/p1",
  assertionLifetimeMinutes: 5, isActive: true, createdAt: "2026-09-26T00:00:00Z", updatedAt: null
};

describe("SAML application form", () => {
  it("accepts HTTPS endpoints, and HTTP only on this computer", () => {
    expect(isBrowserEndpoint("https://crm.example.test/acs")).toBe(true);
    expect(isBrowserEndpoint("http://localhost:3000/acs")).toBe(true);
    expect(isBrowserEndpoint("http://crm.example.test/acs")).toBe(false);
    expect(isBrowserEndpoint("https://crm.example.test/acs#fragment")).toBe(false);
    expect(isBrowserEndpoint("not a url")).toBe(false);
  });

  it("round-trips a stored application into versioned update requests", () => {
    const values = samlAppDefaults(provider);
    expect(values.assertionConsumerServiceUrls).toBe("https://crm.example.test/acs\nhttps://crm.example.test/acs2");
    expect(updateSamlAppPayload(values, 3)).toMatchObject({
      assertionConsumerServiceUrls: ["https://crm.example.test/acs", "https://crm.example.test/acs2"],
      singleLogoutServiceUrl: "https://crm.example.test/slo", signingCertificate: null, defaultRelayState: "/home", isActive: true, version: 3
    });
    expect(createSamlAppPayload(values)).toMatchObject({ applicationSystemId: provider.applicationSystemId, entityId: provider.entityId });
  });

  it("refuses what the server would refuse", () => {
    const values = samlAppDefaults(provider);
    const issues = (candidate: typeof values) => samlAppSchema.safeParse(candidate).error?.issues.map((issue) => issue.path.join(".")) ?? [];
    expect(issues({ ...values, assertionConsumerServiceUrls: "http://crm.example.test/acs" })).toContain("assertionConsumerServiceUrls");
    expect(issues({ ...values, requireSignedRequests: true })).toContain("signingCertificate");
    expect(issues({ ...values, encryptAssertions: true })).toContain("encryptionCertificate");
    expect(issues({ ...values, attributes: [{ name: "mail", source: "email" }, { name: "MAIL", source: "name" }] })).toContain("attributes.1.name");
    expect(issues({ ...values, entityId: "has spaces" })).toContain("entityId");
    expect(newSamlAppSchema.safeParse({ ...values, applicationSystemId: "" }).success).toBe(false);
  });

  it("fills the form from metadata and keeps what it does not say", () => {
    const filled = applyMetadata(samlAppDefaults(), {
      entityId: "https://hr.example.test/saml", assertionConsumerServiceUrls: ["https://hr.example.test/acs"], singleLogoutServiceUrl: null,
      nameIdFormat: "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent", signingCertificate: "-----BEGIN CERTIFICATE-----x", encryptionCertificate: null,
      requireSignedRequests: true, warnings: []
    });
    expect(filled).toMatchObject({ entityId: "https://hr.example.test/saml", assertionConsumerServiceUrls: "https://hr.example.test/acs", requireSignedRequests: true, singleLogoutServiceUrl: "" });
    expect(filled.attributes).toEqual(samlAppDefaults().attributes);
  });

  it("describes attribute sources", () => {
    expect(describeSource("roles")).toBe("Roles en la aplicación");
    expect(describeSource("profile:department")).toBe("Perfil: department");
  });
});
