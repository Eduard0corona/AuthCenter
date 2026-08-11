import test from "node:test";
import assert from "node:assert/strict";
import { AuthCenterClient } from "../dist/index.js";

test("PKCE is S256 and verifier is high entropy", async () => {
  const pkce = await AuthCenterClient.createPkce();
  assert.match(pkce.verifier, /^[A-Za-z0-9_-]{43,128}$/);
  assert.match(pkce.challenge, /^[A-Za-z0-9_-]{43}$/);
  assert.notEqual(pkce.verifier, pkce.challenge);
});

test("authorization URL encodes exact OIDC and PKCE values", async () => {
  const client = new AuthCenterClient({ authority: "https://identity.example.test", clientId: "web", redirectUri: "https://app.example.test/callback" });
  const pkce = await AuthCenterClient.createPkce();
  const url = client.authorizationUrl({ state: "state-value", nonce: "nonce-value", pkce });
  assert.equal(url.origin, "https://identity.example.test");
  assert.equal(url.searchParams.get("code_challenge_method"), "S256");
  assert.equal(url.searchParams.get("redirect_uri"), "https://app.example.test/callback");
  assert.equal(url.searchParams.get("state"), "state-value");
});

test("insecure authority is rejected", () => {
  assert.throws(() => new AuthCenterClient({ authority: "http://identity.example.test", clientId: "web", redirectUri: "https://app.example.test/callback" }), /HTTPS/);
});
