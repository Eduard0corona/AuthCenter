import { describe, expect, it } from "vitest";
import type { FederationRoutingRule, ProfileAttributeDefinition } from "../../api/types";
import { federationProviderDefaults, federationProviderPayload, federationProviderSchema, moveRule, orderChanged, reorderPayload, routingRulePayload, routingRuleSchema } from "./federation";

const applicationId = "11111111-1111-4111-8111-111111111111";
const providerId = "22222222-2222-4222-8222-222222222222";
const pem = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----";

describe("federation provider form", () => {
  it("requires OIDC client and exact HTTPS callback, clearing SAML fields in the payload", () => {
    const values = { ...federationProviderDefaults(), applicationSystemId: applicationId, name: "Corporate", issuer: "https://login.example.test/", clientId: "authcenter", oidcCallbackUrl: "https://authcenter.example.test/api/federation/oidc/callback", samlSingleSignOnUrl: "https://stale.test" };
    expect(federationProviderPayload(values, 3)).toMatchObject({ protocol: "Oidc", clientId: "authcenter", clientSecret: null, samlSingleSignOnUrl: null, samlSigningCertificatePem: null, version: 3 });
    expect(federationProviderSchema.safeParse({ ...values, oidcCallbackUrl: "http://insecure.test/cb" }).success).toBe(false);
    expect(federationProviderSchema.safeParse({ ...values, issuer: "not a url" }).success).toBe(false);
  });

  it("requires a PEM certificate for new SAML providers but not when one is already stored", () => {
    const saml = { ...federationProviderDefaults(), applicationSystemId: applicationId, name: "IdP", protocol: "Saml2" as const, issuer: "https://idp.test", samlSingleSignOnUrl: "https://idp.test/sso" };
    expect(federationProviderSchema.safeParse(saml).success).toBe(false);
    expect(federationProviderSchema.safeParse({ ...saml, samlSigningCertificatePem: "garbage" }).success).toBe(false);
    expect(federationProviderPayload({ ...saml, samlSigningCertificatePem: pem })).toMatchObject({ samlSigningCertificatePem: pem, clientId: null, oidcCallbackUrl: null });
    expect(federationProviderPayload({ ...saml, hasStoredCertificate: true }, 2)).toMatchObject({ samlSigningCertificatePem: null, version: 2 });
  });
});

describe("routing rules", () => {
  const definition = { id: "66666666-6666-4666-8666-666666666666", dataType: "Integer" } as ProfileAttributeDefinition;
  const base = { federationProviderId: providerId, priority: 10, emailDomain: "", directoryGroupId: "", profileAttributeDefinitionId: "", expectedValue: "", isActive: true };

  it("requires at least one condition and a valid domain", () => {
    expect(routingRuleSchema.safeParse(base).success).toBe(false);
    expect(routingRuleSchema.safeParse({ ...base, emailDomain: "user@empresa.com" }).success).toBe(false);
    expect(routingRuleSchema.safeParse({ ...base, emailDomain: "Empresa.COM" }).success).toBe(true);
    expect(routingRuleSchema.safeParse({ ...base, priority: 0, emailDomain: "empresa.com" }).success).toBe(false);
  });

  it("serialises the expected profile value with the attribute type", () => {
    const created = routingRulePayload({ ...base, profileAttributeDefinitionId: definition.id, expectedValue: "3" }, definition);
    expect(created).toEqual({ ok: true, payload: { federationProviderId: providerId, priority: 10, emailDomain: null, directoryGroupId: null, profileAttributeDefinitionId: definition.id, expectedProfileValueJson: "3", isActive: true } });
    expect(routingRulePayload({ ...base, profileAttributeDefinitionId: definition.id, expectedValue: "x" }, definition).ok).toBe(false);
    const updated = routingRulePayload({ ...base, emailDomain: "Empresa.com" }, undefined, 4);
    expect(updated).toEqual({ ok: true, payload: { priority: 10, emailDomain: "empresa.com", directoryGroupId: null, profileAttributeDefinitionId: null, expectedProfileValueJson: null, isActive: true, version: 4 } });
  });

  it("reorders locally and re-issues priorities in steps of ten", () => {
    const rule = (id: string, priority: number): FederationRoutingRule => ({ id, federationProviderId: providerId, providerName: "IdP", applicationSystemId: applicationId, priority, emailDomain: null, directoryGroupId: null, profileAttributeDefinitionId: null, expectedProfileValueJson: null, isActive: true, version: 1 });
    const original = [rule("a", 10), rule("b", 20), rule("c", 30)];
    const moved = moveRule(original, "c", -1);
    expect(moved.map((item) => item.id)).toEqual(["a", "c", "b"]);
    expect(moveRule(original, "a", -1)).toBe(original);
    expect(orderChanged(original, moved)).toBe(true);
    expect(orderChanged(original, [...original])).toBe(false);
    expect(reorderPayload(moved)).toEqual({ rules: [{ id: "a", priority: 10, version: 1 }, { id: "c", priority: 20, version: 1 }, { id: "b", priority: 30, version: 1 }] });
  });
});
