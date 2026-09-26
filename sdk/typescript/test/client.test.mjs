import test from "node:test";
import assert from "node:assert/strict";
import { AuthCenterClient, AuthCenterError } from "../dist/index.js";

const authority = "https://identity.example.test/auth";
const redirectUri = "https://app.example.test/callback";

class MemoryStorage {
  #items = new Map();
  getItem(key) { return this.#items.has(key) ? this.#items.get(key) : null; }
  setItem(key, value) { this.#items.set(key, String(value)); }
  removeItem(key) { this.#items.delete(key); }
  get size() { return this.#items.size; }
}

const base64Url = (bytes) => Buffer.from(bytes).toString("base64url");
const encodeJson = (value) => base64Url(new TextEncoder().encode(JSON.stringify(value)));

/** A fake AuthCenter: discovery, JWKS, token, userinfo and revocation endpoints over a fetch stub. */
async function createAuthCenter({ issuer = authority } = {}) {
  const pair = await crypto.subtle.generateKey({ name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" }, true, ["sign", "verify"]);
  const publicJwk = { ...(await crypto.subtle.exportKey("jwk", pair.publicKey)), kid: "key-1", use: "sig", alg: "RS256" };
  const state = { requests: [], idTokenClaims: {}, tokenResponse: null, discoveryIssuer: issuer };

  async function sign(claims, header = { alg: "RS256", kid: "key-1", typ: "JWT" }) {
    const input = `${encodeJson(header)}.${encodeJson(claims)}`;
    const signature = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", pair.privateKey, new TextEncoder().encode(input));
    return `${input}.${base64Url(new Uint8Array(signature))}`;
  }

  const fetch = async (url, init = {}) => {
    const target = new URL(url);
    state.requests.push({ url: target, init });
    const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
    switch (target.pathname) {
      case "/auth/.well-known/openid-configuration":
        return json({
          issuer: state.discoveryIssuer,
          authorization_endpoint: `${authority}/oauth/authorize`,
          token_endpoint: `${authority}/oauth/token`,
          jwks_uri: `${authority}/.well-known/jwks.json`,
          userinfo_endpoint: `${authority}/oauth/userinfo`,
          end_session_endpoint: `${authority}/oauth/logout`,
          revocation_endpoint: `${authority}/oauth/revoke`
        });
      case "/auth/.well-known/jwks.json":
        return json({ keys: [publicJwk] });
      case "/auth/oauth/token": {
        const form = new URLSearchParams(init.body);
        if (state.tokenResponse) return state.tokenResponse(form);
        const now = Math.floor(Date.now() / 1000);
        const idToken = await sign({ iss: issuer, sub: "user-1", aud: "spa", exp: now + 300, iat: now, auth_time: now, nonce: state.lastNonce, sid: "session-1", amr: ["pwd"], email: "ana@example.test", ...state.idTokenClaims });
        return json({ access_token: "access-token", token_type: "Bearer", expires_in: 900, id_token: idToken, refresh_token: "refresh-token", scope: form.get("scope") ?? "openid profile email" });
      }
      case "/auth/oauth/userinfo":
        return init.headers?.authorization === "Bearer access-token" ? json({ sub: "user-1", email: "ana@example.test" }) : json({ error: "invalid_token" }, 401);
      case "/auth/oauth/revoke":
        return new Response(null, { status: 200 });
      default:
        return json({ error: "not_found" }, 404);
    }
  };
  return { fetch, state, sign };
}

function createClient(authCenter, storage = new MemoryStorage(), extra = {}) {
  return new AuthCenterClient({ authority, clientId: "spa", redirectUri, postLogoutRedirectUri: "https://app.example.test/", fetch: authCenter.fetch, storage, ...extra });
}

/** Starts a sign-in and answers it like AuthCenter would, returning the callback URL. */
async function signIn(client, authCenter, options) {
  const url = await client.createSignInUrl(options);
  authCenter.state.lastNonce = url.searchParams.get("nonce");
  return { url, callback: `${redirectUri}?code=code-1&state=${encodeURIComponent(url.searchParams.get("state"))}` };
}

test("PKCE is S256 and verifier is high entropy", async () => {
  const pkce = await AuthCenterClient.createPkce();
  assert.match(pkce.verifier, /^[A-Za-z0-9_-]{43,128}$/);
  assert.match(pkce.challenge, /^[A-Za-z0-9_-]{43}$/);
  assert.notEqual(pkce.verifier, pkce.challenge);
});

test("insecure authority is rejected", () => {
  assert.throws(() => new AuthCenterClient({ authority: "http://identity.example.test", clientId: "web", redirectUri }), /HTTPS/);
});

test("the sign-in URL comes from discovery, keeps the path base and carries every requested parameter", async () => {
  const authCenter = await createAuthCenter();
  const storage = new MemoryStorage();
  const client = createClient(authCenter, storage);
  const url = await client.createSignInUrl({ prompt: "login", maxAge: 300, loginHint: "ana@example.test", identityProvider: "11111111-1111-4111-8111-111111111111", domainHint: "contoso.com", resources: ["https://orders.example.test/api"], scopes: ["openid", "orders.read"] });
  assert.equal(`${url.origin}${url.pathname}`, `${authority}/oauth/authorize`);
  for (const [name, value] of [["client_id", "spa"], ["redirect_uri", redirectUri], ["code_challenge_method", "S256"], ["prompt", "login"], ["max_age", "300"], ["login_hint", "ana@example.test"], ["idp", "11111111-1111-4111-8111-111111111111"], ["domain_hint", "contoso.com"], ["resource", "https://orders.example.test/api"], ["scope", "openid orders.read"]])
    assert.equal(url.searchParams.get(name), value, name);
  assert.equal(storage.size, 1);
});

test("the callback redeems the code with PKCE and validates the ID token", async () => {
  const authCenter = await createAuthCenter();
  const storage = new MemoryStorage();
  const client = createClient(authCenter, storage);
  const { url, callback } = await signIn(client, authCenter, { returnTo: "/orders?tab=open" });

  const result = await client.handleCallback(callback);
  assert.equal(result.claims.sub, "user-1");
  assert.equal(result.claims.sid, "session-1");
  assert.equal(result.tokens.access_token, "access-token");
  assert.equal(result.returnTo, "/orders?tab=open");
  const tokenRequest = authCenter.state.requests.find((request) => request.url.pathname === "/auth/oauth/token");
  const form = new URLSearchParams(tokenRequest.init.body);
  assert.equal(form.get("grant_type"), "authorization_code");
  assert.equal(form.get("redirect_uri"), redirectUri);
  assert.ok(form.get("code_verifier"));
  assert.notEqual(form.get("code_verifier"), url.searchParams.get("code_challenge"));
  assert.equal(storage.size, 0);

  // The state is single-use.
  await assert.rejects(client.handleCallback(callback), (error) => error instanceof AuthCenterError && error.code === "invalid_state");
});

test("the callback rejects ID tokens with the wrong nonce, audience, issuer, lifetime or signature", async () => {
  const cases = [
    [{ nonce: "other-nonce" }, /nonce/],
    [{ aud: "another-client" }, /another client/],
    [{ aud: ["spa", "api"] }, /not authorized/],
    [{ iss: "https://evil.example.test" }, /another issuer/],
    [{ exp: Math.floor(Date.now() / 1000) - 600 }, /expired/]
  ];
  for (const [claims, message] of cases) {
    const authCenter = await createAuthCenter();
    authCenter.state.idTokenClaims = claims;
    const client = createClient(authCenter);
    const { callback } = await signIn(client, authCenter);
    await assert.rejects(client.handleCallback(callback), (error) => error instanceof AuthCenterError && error.code === "invalid_token" && message.test(error.message), JSON.stringify(claims));
  }

  const tampered = await createAuthCenter();
  tampered.state.tokenResponse = async () => {
    const now = Math.floor(Date.now() / 1000);
    const token = await tampered.sign({ iss: authority, sub: "user-1", aud: "spa", exp: now + 300, iat: now, nonce: tampered.state.lastNonce });
    const [header, , signature] = token.split(".");
    const forged = `${header}.${encodeJson({ iss: authority, sub: "admin", aud: "spa", exp: now + 300, iat: now, nonce: tampered.state.lastNonce })}.${signature}`;
    return new Response(JSON.stringify({ access_token: "a", token_type: "Bearer", expires_in: 60, id_token: forged }), { status: 200, headers: { "content-type": "application/json" } });
  };
  const client = createClient(tampered);
  const { callback } = await signIn(client, tampered);
  await assert.rejects(client.handleCallback(callback), /signature is invalid/);
});

test("max_age requires a recent auth_time", async () => {
  const authCenter = await createAuthCenter();
  authCenter.state.idTokenClaims = { auth_time: Math.floor(Date.now() / 1000) - 3600 };
  const client = createClient(authCenter);
  const { callback } = await signIn(client, authCenter, { maxAge: 60 });
  await assert.rejects(client.handleCallback(callback), /older than max_age/);
});

test("AuthCenter errors on the callback, such as login_required, reach the application", async () => {
  const authCenter = await createAuthCenter();
  const client = createClient(authCenter);
  const url = await client.createSignInUrl({ prompt: "none" });
  const callback = `${redirectUri}?error=login_required&error_description=The+user+must+sign+in.&state=${encodeURIComponent(url.searchParams.get("state"))}`;
  await assert.rejects(client.handleCallback(callback), (error) => error instanceof AuthCenterError && error.code === "login_required");
});

test("discovery from another issuer is refused", async () => {
  const authCenter = await createAuthCenter();
  authCenter.state.discoveryIssuer = "https://evil.example.test";
  await assert.rejects(createClient(authCenter).createSignInUrl(), (error) => error.code === "invalid_discovery");
});

test("unsafe return paths are dropped", async () => {
  for (const returnTo of ["https://evil.example.test", "//evil.example.test", "/\\evil.example.test"]) {
    const authCenter = await createAuthCenter();
    const client = createClient(authCenter);
    const { callback } = await signIn(client, authCenter, { returnTo });
    assert.equal((await client.handleCallback(callback)).returnTo, null, returnTo);
  }
});

test("userinfo, refresh, revocation and logout use the discovered endpoints", async () => {
  const authCenter = await createAuthCenter();
  const client = createClient(authCenter);
  assert.deepEqual(await client.userInfo("access-token"), { sub: "user-1", email: "ana@example.test" });
  await assert.rejects(client.userInfo("wrong"), (error) => error.code === "invalid_token" && error.status === 401);

  authCenter.state.tokenResponse = (form) => new Response(JSON.stringify({ access_token: `refreshed-for-${form.get("resource")}`, token_type: "Bearer", expires_in: 900 }), { status: 200, headers: { "content-type": "application/json" } });
  assert.equal((await client.refresh("refresh-token", { resource: "https://orders.example.test/api" })).access_token, "refreshed-for-https://orders.example.test/api");

  await client.revoke("refresh-token", "refresh_token");
  const revocation = authCenter.state.requests.find((request) => request.url.pathname === "/auth/oauth/revoke");
  assert.equal(new URLSearchParams(revocation.init.body).get("token_type_hint"), "refresh_token");

  const logout = await client.endSessionUrl({ idTokenHint: "id-token", state: "xyz" });
  assert.equal(`${logout.origin}${logout.pathname}`, `${authority}/oauth/logout`);
  assert.equal(logout.searchParams.get("post_logout_redirect_uri"), "https://app.example.test/");
  assert.equal(logout.searchParams.get("id_token_hint"), "id-token");
  assert.equal(logout.searchParams.get("state"), "xyz");
});

test("the deprecated synchronous URL keeps the authority path base", async () => {
  const client = new AuthCenterClient({ authority, clientId: "web", redirectUri });
  const url = client.authorizationUrl({ state: "state-value", nonce: "nonce-value", pkce: await AuthCenterClient.createPkce() });
  assert.equal(`${url.origin}${url.pathname}`, `${authority}/oauth/authorize`);
  assert.equal(url.searchParams.get("state"), "state-value");
});
