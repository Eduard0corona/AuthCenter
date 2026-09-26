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
    expect(federationProviderSchema.safeParse({ ...values, issuer: "http://login.example.test" }).success).toBe(false);
    // A blank callback lets the server use AuthCenter's hosted callback.
    expect(federationProviderPayload({ ...values, oidcCallbackUrl: "" })).toMatchObject({ oidcCallbackUrl: null, requireVerifiedEmail: true, trustUpstreamMfa: false, groupsClaim: null, groupMappings: [] });
  });

  it("accepts non-HTTPS SAML entity IDs and validates group mappings", () => {
    const saml = { ...federationProviderDefaults(), applicationSystemId: applicationId, name: "AD FS", protocol: "Saml2" as const, samlSingleSignOnUrl: "https://idp.test/sso", samlSigningCertificatePem: pem };
    expect(federationProviderSchema.safeParse({ ...saml, issuer: "http://adfs.example.test/adfs/services/trust" }).success).toBe(true);
    expect(federationProviderSchema.safeParse({ ...saml, issuer: "urn:example:idp" }).success).toBe(true);
    expect(federationProviderSchema.safeParse({ ...saml, issuer: "adfs" }).success).toBe(false);

    const mapping = { upstreamValue: "Engineering", directoryGroupId: "55555555-5555-4555-8555-555555555555" };
    expect(federationProviderSchema.safeParse({ ...saml, issuer: "urn:example:idp", groupMappings: [mapping] }).success).toBe(false);
    expect(federationProviderSchema.safeParse({ ...saml, issuer: "urn:example:idp", groupsClaim: "groups", groupMappings: [mapping, { ...mapping, upstreamValue: "engineering" }] }).success).toBe(false);
    expect(federationProviderPayload({ ...saml, issuer: "urn:example:idp", groupsClaim: "groups", trustUpstreamMfa: true, groupMappings: [mapping] }))
      .toMatchObject({ requireVerifiedEmail: true, trustUpstreamMfa: true, groupsClaim: "groups", groupMappings: [mapping] });
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
  const stored = (overrides: Partial<FederationRoutingRule>): FederationRoutingRule => ({ id: "r", federationProviderId: providerId, providerName: "IdP", applicationSystemId: applicationId, priority: 10, emailDomain: null, directoryGroupId: null, profileAttributeDefinitionId: null, expectedProfileValueJson: null, isActive: true, version: 1, ...overrides });
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
    const updated = routingRulePayload({ ...base, emailDomain: "Empresa.com" }, undefined, stored({ version: 4 }));
    expect(updated).toEqual({ ok: true, payload: { priority: 10, emailDomain: "empresa.com", directoryGroupId: null, profileAttributeDefinitionId: null, expectedProfileValueJson: null, isActive: true, version: 4 } });
  });

  it("keeps the stored attribute JSON verbatim when the schema is not readable, and refuses retyping it", () => {
    const original = stored({ profileAttributeDefinitionId: definition.id, expectedProfileValueJson: "3", directoryGroupId: "55555555-5555-4555-8555-555555555555", version: 2 });
    const values = { ...base, emailDomain: "empresa.com", directoryGroupId: original.directoryGroupId!, profileAttributeDefinitionId: definition.id, expectedValue: "3" };
    expect(routingRulePayload(values, undefined, original)).toEqual({ ok: true, payload: { priority: 10, emailDomain: "empresa.com", directoryGroupId: original.directoryGroupId, profileAttributeDefinitionId: definition.id, expectedProfileValueJson: "3", isActive: true, version: 2 } });
    const changed = routingRulePayload({ ...values, expectedValue: "4" }, undefined, original);
    expect(changed.ok).toBe(false);
    expect(changed.ok ? "" : changed.error).toContain("AUTHCENTER_PROFILE_SCHEMAS_READ");
    expect(routingRulePayload({ ...values, expectedValue: "4" }, definition, original)).toMatchObject({ ok: true, payload: { expectedProfileValueJson: "4" } });
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
