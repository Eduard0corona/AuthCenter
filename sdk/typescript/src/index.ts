/**
 * Dependency-free OAuth 2.0 / OpenID Connect client for AuthCenter: authorization code with PKCE,
 * callback handling with ID token validation (signature, issuer, audience, lifetime, nonce),
 * refresh, revocation, UserInfo and RP-initiated logout. Endpoints come from discovery.
 */

export interface AuthCenterOptions {
  /** HTTPS issuer of AuthCenter; it may include a path base (https://host/identity). */
  authority: string;
  clientId: string;
  redirectUri: string;
  /** Registered post-logout redirect URI used by signOutRedirect/endSessionUrl by default. */
  postLogoutRedirectUri?: string;
  scopes?: readonly string[];
  fetch?: typeof globalThis.fetch;
  /** Keeps sign-in transactions (state, nonce, PKCE verifier) until the callback. Default: sessionStorage. */
  storage?: TransactionStorage;
  /** Tolerated clock difference when validating tokens, in seconds (default 60). */
  clockSkewSeconds?: number;
  /** Current time in milliseconds; for tests. */
  now?: () => number;
}

/** The subset of the Web Storage API the client needs. */
export interface TransactionStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export interface PkcePair { verifier: string; challenge: string; }

export interface TokenSet {
  access_token: string;
  token_type: "Bearer";
  expires_in: number;
  refresh_token?: string;
  id_token?: string;
  scope?: string;
  issued_token_type?: string;
}

export interface DiscoveryDocument {
  issuer: string;
  authorization_endpoint: string;
  token_endpoint: string;
  jwks_uri: string;
  userinfo_endpoint?: string;
  end_session_endpoint?: string;
  revocation_endpoint?: string;
  introspection_endpoint?: string;
}

export interface IdTokenClaims {
  iss: string;
  sub: string;
  aud: string | string[];
  exp: number;
  iat: number;
  nbf?: number;
  nonce?: string;
  auth_time?: number;
  sid?: string;
  amr?: string[];
  acr?: string;
  azp?: string;
  email?: string;
  email_verified?: boolean;
  name?: string;
  [claim: string]: unknown;
}

export interface SignInOptions {
  scopes?: readonly string[];
  prompt?: "none" | "login" | "consent" | "select_account";
  /** Maximum authentication age in seconds; the ID token's auth_time is checked against it. */
  maxAge?: number;
  loginHint?: string;
  acrValues?: string;
  /** AuthCenter federation provider ID: the hosted login goes straight to it (idp). */
  identityProvider?: string;
  /** Email domain for home realm discovery (domain_hint). */
  domainHint?: string;
  /** RFC 8707 resource indicators of the APIs whose scopes are requested. */
  resources?: readonly string[];
  /** Local path to return to after the callback. */
  returnTo?: string;
}

export interface SignInResult {
  tokens: TokenSet;
  /** Validated ID token claims (null when openid was not requested). */
  claims: IdTokenClaims | null;
  /** The local path given to createSignInUrl, if any. */
  returnTo: string | null;
}

export interface EndSessionOptions {
  idTokenHint?: string;
  postLogoutRedirectUri?: string;
  state?: string;
}

/** An OAuth error from AuthCenter or a failed validation, with a stable code. */
export class AuthCenterError extends Error {
  readonly code: string;
  readonly description: string | undefined;
  readonly status: number | undefined;

  constructor(code: string, description?: string, status?: number) {
    super(description ? `${code}: ${description}` : code);
    this.name = "AuthCenterError";
    this.code = code;
    this.description = description;
    this.status = status;
  }
}

interface Transaction { nonce: string; verifier: string; returnTo: string | null; createdAt: number; maxAge: number | null; requestedOpenId: boolean; }
interface JsonWebKeyWithKid extends JsonWebKey { kid?: string; }

const TransactionPrefix = "authcenter.tx.";
const TransactionLifetimeMs = 10 * 60 * 1000;

export class AuthCenterClient {
  readonly #authority: URL;
  readonly #fetch: typeof globalThis.fetch;
  readonly #options: AuthCenterOptions;
  readonly #skew: number;
  readonly #now: () => number;
  #discovery: Promise<DiscoveryDocument> | null = null;
  #keys: Promise<JsonWebKeyWithKid[]> | null = null;

  constructor(options: AuthCenterOptions) {
    this.#authority = new URL(options.authority);
    if (this.#authority.protocol !== "https:") throw new Error("AuthCenter authority must use HTTPS.");
    if (this.#authority.search || this.#authority.hash || this.#authority.username) throw new Error("AuthCenter authority must not carry a query, fragment or credentials.");
    if (!options.clientId) throw new Error("clientId is required.");
    this.#options = options;
    this.#fetch = options.fetch ?? globalThis.fetch.bind(globalThis);
    this.#skew = options.clockSkewSeconds ?? 60;
    this.#now = options.now ?? (() => Date.now());
  }

  /** Reads and validates the discovery document once (issuer must be this authority, endpoints HTTPS). */
  discover(): Promise<DiscoveryDocument> {
    this.#discovery ??= this.#loadDiscovery().catch((error: unknown) => { this.#discovery = null; throw error; });
    return this.#discovery;
  }

  /** Creates the authorization request and remembers its state, nonce and PKCE verifier for the callback. */
  async createSignInUrl(options: SignInOptions = {}): Promise<URL> {
    const discovery = await this.discover();
    const pkce = await AuthCenterClient.createPkce();
    const state = AuthCenterClient.randomState();
    const nonce = AuthCenterClient.randomState();
    const scopes = options.scopes ?? this.#options.scopes ?? ["openid", "profile", "email"];
    const parameters = new URLSearchParams({
      response_type: "code",
      client_id: this.#options.clientId,
      redirect_uri: this.#options.redirectUri,
      scope: scopes.join(" "),
      state,
      nonce,
      code_challenge: pkce.challenge,
      code_challenge_method: "S256"
    });
    if (options.prompt) parameters.set("prompt", options.prompt);
    if (options.maxAge !== undefined) parameters.set("max_age", String(Math.max(0, Math.trunc(options.maxAge))));
    if (options.loginHint) parameters.set("login_hint", options.loginHint);
    if (options.acrValues) parameters.set("acr_values", options.acrValues);
    if (options.identityProvider) parameters.set("idp", options.identityProvider);
    if (options.domainHint) parameters.set("domain_hint", options.domainHint);
    for (const resource of options.resources ?? []) parameters.append("resource", resource);

    const transaction: Transaction = {
      nonce,
      verifier: pkce.verifier,
      returnTo: localPath(options.returnTo),
      createdAt: this.#now(),
      maxAge: options.maxAge ?? null,
      requestedOpenId: scopes.includes("openid")
    };
    this.#storage().setItem(TransactionPrefix + state, JSON.stringify(transaction));
    const url = new URL(discovery.authorization_endpoint);
    url.search = parameters.toString();
    return url;
  }

  /** Browser helper: navigates to AuthCenter to sign in. */
  async signInRedirect(options: SignInOptions = {}): Promise<void> {
    globalThis.location.assign((await this.createSignInUrl(options)).toString());
  }

  /**
   * Completes the sign-in on the redirect URI: checks the single-use state, turns AuthCenter errors
   * (for example login_required after prompt=none) into AuthCenterError, redeems the code with the
   * PKCE verifier and validates the ID token.
   */
  async handleCallback(url: string | URL = globalThis.location.href): Promise<SignInResult> {
    const callback = new URL(url);
    const state = callback.searchParams.get("state");
    if (!state) throw new AuthCenterError("invalid_request", "The callback has no state.");
    const storage = this.#storage();
    const raw = storage.getItem(TransactionPrefix + state);
    storage.removeItem(TransactionPrefix + state);
    const transaction = raw ? parseTransaction(raw) : null;
    if (!transaction || this.#now() - transaction.createdAt > TransactionLifetimeMs)
      throw new AuthCenterError("invalid_state", "The sign-in response does not match a pending request from this browser.");

    const error = callback.searchParams.get("error");
    if (error) throw new AuthCenterError(error, callback.searchParams.get("error_description") ?? undefined);
    const code = callback.searchParams.get("code");
    if (!code) throw new AuthCenterError("invalid_request", "The callback has no authorization code.");

    const tokens = await this.exchangeCode(code, transaction.verifier);
    let claims: IdTokenClaims | null = null;
    if (tokens.id_token) {
      claims = await this.validateIdToken(tokens.id_token, { nonce: transaction.nonce, ...(transaction.maxAge === null ? {} : { maxAge: transaction.maxAge }) });
    } else if (transaction.requestedOpenId) {
      throw new AuthCenterError("invalid_token", "AuthCenter did not return an ID token for an OpenID Connect request.");
    }
    return { tokens, claims, returnTo: transaction.returnTo };
  }

  /** Validates an AuthCenter ID token: RS256 signature from JWKS, issuer, audience, lifetime, nonce and max_age. */
  async validateIdToken(idToken: string, expected: { nonce?: string; maxAge?: number } = {}): Promise<IdTokenClaims> {
    const parts = idToken.split(".");
    if (parts.length !== 3) throw new AuthCenterError("invalid_token", "The ID token is not a compact JWS.");
    const [encodedHeader, encodedPayload, encodedSignature] = parts as [string, string, string];
    const header = decodeJson(encodedHeader) as { alg?: string; kid?: string; typ?: string };
    const claims = decodeJson(encodedPayload) as IdTokenClaims;
    if (header.alg !== "RS256") throw new AuthCenterError("invalid_token", "The ID token must be signed with RS256.");
    if (typeof header.typ === "string" && header.typ.toLowerCase() === "at+jwt") throw new AuthCenterError("invalid_token", "An access token is not an ID token.");

    const key = await this.#signingKey(header.kid);
    const verified = await crypto.subtle.verify(
      "RSASSA-PKCS1-v1_5",
      key,
      toArrayBuffer(base64UrlDecode(encodedSignature)),
      toArrayBuffer(new TextEncoder().encode(`${encodedHeader}.${encodedPayload}`)));
    if (!verified) throw new AuthCenterError("invalid_token", "The ID token signature is invalid.");

    const discovery = await this.discover();
    const now = Math.floor(this.#now() / 1000);
    const audiences = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
    if (trimSlash(claims.iss) !== trimSlash(discovery.issuer)) throw new AuthCenterError("invalid_token", "The ID token was issued by another issuer.");
    if (!audiences.includes(this.#options.clientId)) throw new AuthCenterError("invalid_token", "The ID token was issued for another client.");
    if (audiences.length > 1 && claims.azp !== this.#options.clientId) throw new AuthCenterError("invalid_token", "The ID token is not authorized for this client.");
    if (typeof claims.exp !== "number" || claims.exp + this.#skew < now) throw new AuthCenterError("invalid_token", "The ID token has expired.");
    if (typeof claims.iat !== "number" || claims.iat - this.#skew > now) throw new AuthCenterError("invalid_token", "The ID token was issued in the future.");
    if (typeof claims.nbf === "number" && claims.nbf - this.#skew > now) throw new AuthCenterError("invalid_token", "The ID token is not valid yet.");
    if (expected.nonce !== undefined && claims.nonce !== expected.nonce) throw new AuthCenterError("invalid_token", "The ID token nonce does not match the request.");
    if (expected.maxAge !== undefined && (typeof claims.auth_time !== "number" || claims.auth_time + expected.maxAge + this.#skew < now))
      throw new AuthCenterError("invalid_token", "The authentication is older than max_age.");
    if (typeof claims.sub !== "string" || !claims.sub) throw new AuthCenterError("invalid_token", "The ID token has no subject.");
    return claims;
  }

  exchangeCode(code: string, verifier: string, signal?: AbortSignal): Promise<TokenSet> {
    return this.#token({ grant_type: "authorization_code", code, code_verifier: verifier, redirect_uri: this.#options.redirectUri }, signal);
  }

  /** Refreshes a grant; with a resource, asks for a token to that API (RFC 8707). */
  refresh(refreshToken: string, options: { resource?: string; scopes?: readonly string[]; signal?: AbortSignal } = {}): Promise<TokenSet> {
    const values: Record<string, string> = { grant_type: "refresh_token", refresh_token: refreshToken };
    if (options.resource) values.resource = options.resource;
    if (options.scopes?.length) values.scope = options.scopes.join(" ");
    return this.#token(values, options.signal);
  }

  async revoke(token: string, tokenTypeHint?: "refresh_token" | "access_token", signal?: AbortSignal): Promise<void> {
    const discovery = await this.discover();
    const endpoint = discovery.revocation_endpoint ?? new URL("oauth/revoke", withSlash(this.#authority)).toString();
    const values: Record<string, string> = { client_id: this.#options.clientId, token };
    if (tokenTypeHint) values.token_type_hint = tokenTypeHint;
    const response = await this.#send(endpoint, formRequest(values, signal));
    if (!response.ok) throw await oauthError(response);
  }

  /** OpenID Connect UserInfo for an access token issued with openid. */
  async userInfo(accessToken: string, signal?: AbortSignal): Promise<Record<string, unknown>> {
    const discovery = await this.discover();
    if (!discovery.userinfo_endpoint) throw new AuthCenterError("unsupported", "AuthCenter does not publish a UserInfo endpoint.");
    const request: RequestInit = { headers: { authorization: `Bearer ${accessToken}` } };
    if (signal) request.signal = signal;
    const response = await this.#send(discovery.userinfo_endpoint, request);
    if (!response.ok) throw await oauthError(response);
    return await response.json() as Record<string, unknown>;
  }

  /** RP-initiated logout URL (end_session_endpoint) that ends the AuthCenter single sign-on session. */
  async endSessionUrl(options: EndSessionOptions = {}): Promise<URL> {
    const discovery = await this.discover();
    if (!discovery.end_session_endpoint) throw new AuthCenterError("unsupported", "AuthCenter does not publish an end_session_endpoint.");
    const url = new URL(discovery.end_session_endpoint);
    url.searchParams.set("client_id", this.#options.clientId);
    if (options.idTokenHint) url.searchParams.set("id_token_hint", options.idTokenHint);
    const postLogout = options.postLogoutRedirectUri ?? this.#options.postLogoutRedirectUri;
    if (postLogout) url.searchParams.set("post_logout_redirect_uri", postLogout);
    if (options.state) url.searchParams.set("state", options.state);
    return url;
  }

  /** Browser helper: navigates to AuthCenter's logout. */
  async signOutRedirect(options: EndSessionOptions = {}): Promise<void> {
    globalThis.location.assign((await this.endSessionUrl(options)).toString());
  }

  /**
   * Synchronous authorization URL for callers that manage state, nonce and PKCE themselves.
   * @deprecated Use createSignInUrl, which also validates the callback and the ID token.
   */
  authorizationUrl(input: { state: string; nonce: string; pkce: PkcePair; scopes?: readonly string[] }): URL {
    if (!input.state || !input.nonce) throw new Error("state and nonce are required.");
    const url = new URL("oauth/authorize", withSlash(this.#authority));
    url.search = new URLSearchParams({
      response_type: "code", client_id: this.#options.clientId, redirect_uri: this.#options.redirectUri,
      scope: (input.scopes ?? this.#options.scopes ?? ["openid", "profile", "email"]).join(" "),
      state: input.state, nonce: input.nonce, code_challenge: input.pkce.challenge, code_challenge_method: "S256"
    }).toString();
    return url;
  }

  static async createPkce(): Promise<PkcePair> {
    const verifier = base64UrlEncode(crypto.getRandomValues(new Uint8Array(64)));
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
    return { verifier, challenge: base64UrlEncode(new Uint8Array(digest)) };
  }

  static randomState(bytes = 32): string { return base64UrlEncode(crypto.getRandomValues(new Uint8Array(bytes))); }

  async #loadDiscovery(): Promise<DiscoveryDocument> {
    const response = await this.#send(new URL(".well-known/openid-configuration", withSlash(this.#authority)).toString(), {});
    if (!response.ok) throw await oauthError(response);
    const document = await response.json() as Partial<DiscoveryDocument>;
    if (typeof document.issuer !== "string" || trimSlash(document.issuer) !== trimSlash(this.#authority.toString()))
      throw new AuthCenterError("invalid_discovery", "The discovery document belongs to another issuer.");
    for (const name of ["authorization_endpoint", "token_endpoint", "jwks_uri"] as const)
      if (!isHttps(document[name])) throw new AuthCenterError("invalid_discovery", `The discovery document has no HTTPS ${name}.`);
    for (const name of ["userinfo_endpoint", "end_session_endpoint", "revocation_endpoint", "introspection_endpoint"] as const)
      if (document[name] !== undefined && !isHttps(document[name])) throw new AuthCenterError("invalid_discovery", `The discovery ${name} is not HTTPS.`);
    return document as DiscoveryDocument;
  }

  async #signingKey(kid: string | undefined): Promise<CryptoKey> {
    const find = async (refresh: boolean) => {
      if (refresh) this.#keys = null;
      this.#keys ??= this.#loadKeys().catch((error: unknown) => { this.#keys = null; throw error; });
      const keys = await this.#keys;
      return keys.find((key) => key.kty === "RSA" && (key.use === undefined || key.use === "sig") && (kid === undefined || key.kid === kid));
    };
    // A key AuthCenter rotated in after the JWKS was cached triggers one refresh.
    const jwk = await find(false) ?? await find(true);
    if (!jwk?.n || !jwk.e) throw new AuthCenterError("invalid_token", "The ID token was signed with an unknown key.");
    const publicKey: JsonWebKey = { kty: "RSA", n: jwk.n, e: jwk.e, alg: "RS256", ext: true };
    return crypto.subtle.importKey("jwk", publicKey, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["verify"]);
  }

  async #loadKeys(): Promise<JsonWebKeyWithKid[]> {
    const response = await this.#send((await this.discover()).jwks_uri, {});
    if (!response.ok) throw await oauthError(response);
    const document = await response.json() as { keys?: JsonWebKeyWithKid[] };
    return Array.isArray(document.keys) ? document.keys : [];
  }

  async #token(values: Record<string, string>, signal?: AbortSignal): Promise<TokenSet> {
    const discovery = await this.discover();
    const response = await this.#send(discovery.token_endpoint, formRequest({ ...values, client_id: this.#options.clientId }, signal));
    if (!response.ok) throw await oauthError(response);
    return await response.json() as TokenSet;
  }

  async #send(url: string, request: RequestInit): Promise<Response> {
    try { return await this.#fetch(url, request); }
    catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") throw error;
      throw new AuthCenterError("network_error", "AuthCenter could not be reached.");
    }
  }

  #storage(): TransactionStorage {
    const storage = this.#options.storage ?? (globalThis as { sessionStorage?: TransactionStorage }).sessionStorage;
    if (!storage) throw new Error("A transaction storage is required outside the browser (options.storage).");
    return storage;
  }
}

function formRequest(values: Record<string, string>, signal?: AbortSignal): RequestInit {
  const request: RequestInit = {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams(values)
  };
  if (signal) request.signal = signal;
  return request;
}

function parseTransaction(raw: string): Transaction | null {
  try {
    const value = JSON.parse(raw) as Partial<Transaction>;
    return typeof value.nonce === "string" && typeof value.verifier === "string" && typeof value.createdAt === "number"
      ? { nonce: value.nonce, verifier: value.verifier, createdAt: value.createdAt, returnTo: value.returnTo ?? null, maxAge: value.maxAge ?? null, requestedOpenId: value.requestedOpenId ?? true }
      : null;
  } catch {
    return null;
  }
}

/** A path on the application's own origin, never another site (no //host, no backslashes). */
function localPath(value: string | undefined): string | null {
  return typeof value === "string" && value.startsWith("/") && !value.startsWith("//") && !/[\\\u0000-\u001f]/.test(value) ? value : null;
}

function withSlash(url: URL): URL {
  return new URL(url.toString().endsWith("/") ? url.toString() : `${url.toString()}/`);
}

function trimSlash(value: string): string { return value.replace(/\/+$/, ""); }

function isHttps(value: unknown): value is string {
  if (typeof value !== "string") return false;
  try { return new URL(value).protocol === "https:"; } catch { return false; }
}

function base64UrlEncode(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function base64UrlDecode(value: string): Uint8Array {
  if (!/^[A-Za-z0-9_-]*$/.test(value)) throw new AuthCenterError("invalid_token", "The token is not base64url encoded.");
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/");
  const binary = atob(normalized.padEnd(Math.ceil(normalized.length / 4) * 4, "="));
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

function toArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
}

function decodeJson(segment: string): Record<string, unknown> {
  try {
    const value: unknown = JSON.parse(new TextDecoder().decode(base64UrlDecode(segment)));
    if (value && typeof value === "object" && !Array.isArray(value)) return value as Record<string, unknown>;
  } catch (error) {
    if (error instanceof AuthCenterError) throw error;
  }
  throw new AuthCenterError("invalid_token", "The token segment is not a JSON object.");
}

async function oauthError(response: Response): Promise<AuthCenterError> {
  try {
    const body = await response.json() as { error?: string; error_description?: string; errorCode?: string; message?: string };
    return new AuthCenterError(body.error ?? body.errorCode ?? "request_failed", body.error_description ?? body.message, response.status);
  } catch {
    return new AuthCenterError("request_failed", `AuthCenter request failed (${response.status}).`, response.status);
  }
}
