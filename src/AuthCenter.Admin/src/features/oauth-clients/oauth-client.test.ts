import { describe, expect, it } from "vitest";
import { grantLabel, oauthClientPayload, oauthClientSchema } from "./oauth-client";

describe("grantLabel", () => {
  it("names the grants by what they do and keeps unknown ones", () => {
    expect(grantLabel("authorization_code")).toBe("Código de autorización");
    expect(grantLabel("urn:ietf:params:oauth:grant-type:token-exchange")).toBe("Intercambio de tokens");
    expect(grantLabel("urn:example:custom")).toBe("urn:example:custom");
  });
});

const valid = {
  applicationSystemId: "11111111-1111-4111-8111-111111111111",
  clientId: "partner_portal",
  displayName: "Partner Portal",
  clientType: "0" as const,
  redirectUris: "https://partner.example.com/callback\nhttp://localhost:5173/callback",
  allowedScopes: ["openid", "profile", "email", "offline_access"] as const,
  grantTypes: ["authorization_code", "refresh_token"] as const,
  loginUrl: "https://partner.example.com/login",
  apiScopes: "",
  allowedCorsOrigins: "https://partner.example.com",
  postLogoutRedirectUris: "https://partner.example.com/signout-callback-authcenter",
  backchannelLogoutUri: "https://partner.example.com/auth/backchannel-logout",
  accessTokenLifetimeSeconds: 900,
  requirePkce: true,
  autoConsent: false,
  isActive: true
};

describe("OAuth client form", () => {
  it("normalizes exact redirect URI lines for create", () => {
    const parsed = oauthClientSchema.parse(valid);
    expect(oauthClientPayload(parsed, true)).toMatchObject({
      clientId: "partner_portal",
      clientType: 0,
      redirectUris: ["https://partner.example.com/callback", "http://localhost:5173/callback"]
    });
  });

  it("rejects insecure, duplicate and wildcard redirect URIs", () => {
    const result = oauthClientSchema.safeParse({ ...valid, redirectUris: "http://partner.example.com/callback\nhttps://*.example.com/callback\nhttps://*.example.com/callback" });
    expect(result.success).toBe(false);
  });

  it("rejects client credentials for public clients", () => {
    const result = oauthClientSchema.safeParse({ ...valid, clientType: "1", grantTypes: ["client_credentials"], allowedScopes: ["email"], redirectUris: "" });
    expect(result.success).toBe(false);
  });

  it("requires refresh token when offline access is selected", () => {
    const result = oauthClientSchema.safeParse({ ...valid, grantTypes: ["authorization_code"] });
    expect(result.success).toBe(false);
  });

  it("sends logout registration and clears an empty back-channel URI", () => {
    expect(oauthClientPayload(oauthClientSchema.parse(valid), false)).toMatchObject({
      postLogoutRedirectUris: ["https://partner.example.com/signout-callback-authcenter"],
      backchannelLogoutUri: "https://partner.example.com/auth/backchannel-logout"
    });
    expect(oauthClientPayload(oauthClientSchema.parse({ ...valid, backchannelLogoutUri: "" }), false)).toMatchObject({ backchannelLogoutUri: null });
  });

  it("rejects insecure logout URIs and back-channel logout without authorization code", () => {
    expect(oauthClientSchema.safeParse({ ...valid, postLogoutRedirectUris: "http://partner.example.com/bye" }).success).toBe(false);
    expect(oauthClientSchema.safeParse({ ...valid, backchannelLogoutUri: "https://partner.example.com/logout#x" }).success).toBe(false);
    expect(oauthClientSchema.safeParse({
      ...valid, grantTypes: ["client_credentials"], allowedScopes: ["email"], redirectUris: "", postLogoutRedirectUris: "", requirePkce: false
    }).success).toBe(false);
  });

  it("accepts bare origins for CORS and rejects paths or insecure schemes", () => {
    expect(oauthClientPayload(oauthClientSchema.parse(valid), false)).toMatchObject({ allowedCorsOrigins: ["https://partner.example.com"] });
    for (const origin of ["https://partner.example.com/app", "http://partner.example.com", "https://partner.example.com?x=1"])
      expect(oauthClientSchema.safeParse({ ...valid, allowedCorsOrigins: origin }).success).toBe(false);
  });

  it("merges API scopes into the allowed scopes and validates their names", () => {
    expect(oauthClientPayload(oauthClientSchema.parse({ ...valid, apiScopes: "orders.read\nbilling.read" }), false).allowedScopes)
      .toEqual(["openid", "profile", "email", "offline_access", "orders.read", "billing.read"]);
    expect(oauthClientSchema.safeParse({ ...valid, apiScopes: "Orders Read" }).success).toBe(false);
    expect(oauthClientSchema.safeParse({ ...valid, allowedScopes: [], apiScopes: "" }).success).toBe(false);
    expect(oauthClientSchema.safeParse({
      ...valid, clientType: "1", grantTypes: ["urn:ietf:params:oauth:grant-type:token-exchange"], allowedScopes: [], apiScopes: "orders.read", redirectUris: "", postLogoutRedirectUris: "", backchannelLogoutUri: "", requirePkce: false
    }).success).toBe(false);
  });

  it("rejects identity scopes for a machine-only client", () => {
    const result = oauthClientSchema.safeParse({ ...valid, grantTypes: ["client_credentials"], allowedScopes: ["openid", "email"], redirectUris: "", requirePkce: false });
    expect(result.success).toBe(false);
  });

  it("keeps the back-channel session setting chosen for the client", () => {
    expect(oauthClientPayload(oauthClientSchema.parse({ ...valid, backchannelLogoutSessionRequired: false }), false).backchannelLogoutSessionRequired).toBe(false);
    expect(oauthClientPayload(oauthClientSchema.parse(valid), false).backchannelLogoutSessionRequired).toBe(true);
  });
});
