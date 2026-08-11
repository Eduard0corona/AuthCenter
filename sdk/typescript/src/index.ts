export interface AuthCenterOptions {
  authority: string;
  clientId: string;
  redirectUri: string;
  scopes?: readonly string[];
  fetch?: typeof globalThis.fetch;
}

export interface PkcePair { verifier: string; challenge: string; }
export interface TokenSet {
  access_token: string; token_type: "Bearer"; expires_in: number;
  refresh_token?: string; id_token?: string; scope?: string;
}

export class AuthCenterClient {
  readonly #authority: URL;
  readonly #fetch: typeof globalThis.fetch;
  readonly #options: AuthCenterOptions;

  constructor(options: AuthCenterOptions) {
    this.#authority = new URL(options.authority);
    if (this.#authority.protocol !== "https:") throw new Error("AuthCenter authority must use HTTPS.");
    this.#options = options;
    this.#fetch = options.fetch ?? globalThis.fetch.bind(globalThis);
  }

  authorizationUrl(input: { state: string; nonce: string; pkce: PkcePair; scopes?: readonly string[] }): URL {
    if (!input.state || !input.nonce) throw new Error("state and nonce are required.");
    const url = new URL("/oauth/authorize", this.#authority);
    url.search = new URLSearchParams({
      response_type: "code", client_id: this.#options.clientId, redirect_uri: this.#options.redirectUri,
      scope: (input.scopes ?? this.#options.scopes ?? ["openid", "profile", "email"]).join(" "),
      state: input.state, nonce: input.nonce, code_challenge: input.pkce.challenge, code_challenge_method: "S256"
    }).toString();
    return url;
  }

  exchangeCode(code: string, verifier: string, signal?: AbortSignal): Promise<TokenSet> {
    return this.#token({ grant_type: "authorization_code", code, code_verifier: verifier, redirect_uri: this.#options.redirectUri }, signal);
  }

  refresh(refreshToken: string, signal?: AbortSignal): Promise<TokenSet> {
    return this.#token({ grant_type: "refresh_token", refresh_token: refreshToken }, signal);
  }

  async revoke(token: string, signal?: AbortSignal): Promise<void> {
    const request: RequestInit = {
      method: "POST", headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ client_id: this.#options.clientId, token })
    };
    if (signal) request.signal = signal;
    const response = await this.#fetch(new URL("/oauth/revoke", this.#authority), request);
    if (!response.ok) throw await oauthError(response);
  }

  static async createPkce(): Promise<PkcePair> {
    const verifier = base64Url(crypto.getRandomValues(new Uint8Array(64)));
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
    return { verifier, challenge: base64Url(new Uint8Array(digest)) };
  }

  static randomState(bytes = 32): string { return base64Url(crypto.getRandomValues(new Uint8Array(bytes))); }

  async #token(values: Record<string, string>, signal?: AbortSignal): Promise<TokenSet> {
    const request: RequestInit = {
      method: "POST", headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ ...values, client_id: this.#options.clientId })
    };
    if (signal) request.signal = signal;
    const response = await this.#fetch(new URL("/oauth/token", this.#authority), request);
    if (!response.ok) throw await oauthError(response);
    return await response.json() as TokenSet;
  }
}

function base64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

async function oauthError(response: Response): Promise<Error> {
  let detail = `AuthCenter request failed (${response.status}).`;
  try { const body = await response.json() as { error?: string; error_description?: string }; detail = `${body.error ?? "oauth_error"}: ${body.error_description ?? detail}`; } catch { /* keep bounded status-only detail */ }
  return new Error(detail);
}
