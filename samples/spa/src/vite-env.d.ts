/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_AUTHCENTER_AUTHORITY: string;
  readonly VITE_AUTHCENTER_CLIENT_ID: string;
  readonly VITE_AUTHCENTER_SCOPES?: string;
  readonly VITE_API_URL?: string;
  readonly VITE_API_RESOURCE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
