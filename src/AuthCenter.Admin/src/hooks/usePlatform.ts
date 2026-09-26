import { useQuery } from "@tanstack/react-query";
import { apiRequest } from "../api/client";
import type { AdminMetadata, VersionManifest } from "../api/types";

/** Error codes, step-up purposes and the environment the console is connected to. */
export function useAdminMetadata() {
  return useQuery({
    queryKey: ["admin-metadata"],
    queryFn: ({ signal }) => apiRequest<AdminMetadata>("/api/admin-metadata", { signal }),
    staleTime: Number.POSITIVE_INFINITY
  });
}

/** The server build; shown in the shell so support knows which version an operator is using. */
export function useVersion() {
  return useQuery({
    queryKey: ["version"],
    queryFn: ({ signal }) => apiRequest<VersionManifest>("/api/version", { signal }),
    staleTime: Number.POSITIVE_INFINITY
  });
}

export interface EnvironmentDescription {
  label: string;
  tone: "production" | "staging" | "development";
}

export function describeEnvironment(name: string | null | undefined): EnvironmentDescription | null {
  const value = name?.trim();
  if (!value) return null;
  switch (value.toLowerCase()) {
    case "production": return { label: "Producción", tone: "production" };
    case "staging": return { label: "Staging", tone: "staging" };
    case "development": return { label: "Desarrollo", tone: "development" };
    case "testing": case "test": return { label: "Pruebas", tone: "development" };
    default: return { label: value, tone: /prod/i.test(value) ? "production" : "staging" };
  }
}

export function describeVersion(manifest: VersionManifest | undefined): string {
  if (!manifest) return "";
  return manifest.commit ? `v${manifest.version} · ${manifest.commit.slice(0, 7)}` : `v${manifest.version}`;
}
