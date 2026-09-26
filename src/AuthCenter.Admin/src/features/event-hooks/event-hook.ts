import { z } from "zod";
import type { EventHook, EventTypeInfo } from "../../api/types";

export const WILDCARD = "*";

const CATEGORY_LABELS: Record<string, string> = {
  authentication: "Autenticación",
  mfa: "Verificación en dos pasos",
  passkeys: "Passkeys",
  sessions: "Sesiones y dispositivos",
  tokens: "Tokens",
  oauth: "OAuth y OpenID Connect",
  federation: "Federación",
  users: "Usuarios",
  account: "Cuenta del usuario",
  groups: "Grupos",
  "roles-permissions": "Roles y permisos",
  applications: "Aplicaciones",
  "access-policies": "Políticas de acceso",
  profiles: "Perfiles",
  provisioning: "Provisioning",
  "event-hooks": "Event hooks",
  administration: "Administración"
};

export interface EventTypeGroup {
  category: string;
  label: string;
  types: string[];
}

/** The catalog by area, in the order the server lists it. */
export function groupEventTypes(catalog: readonly EventTypeInfo[]): EventTypeGroup[] {
  const groups = new Map<string, EventTypeGroup>();
  for (const { type, category } of catalog) {
    const group = groups.get(category) ?? { category, label: CATEGORY_LABELS[category] ?? category, types: [] };
    group.types.push(type);
    groups.set(category, group);
  }
  return [...groups.values()];
}

/** Groups whose name or types contain the text (case-insensitive), keeping only the matching types. */
export function filterEventTypeGroups(groups: readonly EventTypeGroup[], text: string): EventTypeGroup[] {
  const query = text.trim().toUpperCase();
  if (!query) return [...groups];
  return groups
    .map((group) => group.label.toUpperCase().includes(query) ? group : { ...group, types: group.types.filter((type) => type.includes(query)) })
    .filter((group) => group.types.length > 0);
}

/** A short description of a subscription for lists. */
export function describeSubscription(eventTypes: readonly string[]): string {
  if (eventTypes.includes(WILDCARD)) return "Todos los eventos";
  if (eventTypes.length === 1) return eventTypes[0] ?? "";
  return `${eventTypes.length} tipos de evento`;
}

export const eventHookSchema = z.object({
  name: z.string().trim().min(1, "El nombre es obligatorio.").max(150, "Usa máximo 150 caracteres."),
  url: z.string().trim().min(1, "La URL es obligatoria.").max(1000, "Usa máximo 1000 caracteres.")
    .refine((value) => isHttpsUrl(value), "Usa una URL HTTPS pública, por ejemplo https://hooks.example.com/authcenter."),
  applicationSystemId: z.string(),
  eventTypes: z.array(z.string()).min(1, "Selecciona al menos un tipo de evento.").max(100, "Selecciona como máximo 100 tipos o suscríbete a todos los eventos."),
  isActive: z.boolean()
});

export type EventHookFormValues = z.input<typeof eventHookSchema>;

export function eventHookDefaults(hook?: EventHook): EventHookFormValues {
  return hook
    ? { name: hook.name, url: hook.url, applicationSystemId: hook.applicationSystemId ?? "", eventTypes: [...hook.eventTypes], isActive: hook.isActive }
    : { name: "", url: "", applicationSystemId: "", eventTypes: [], isActive: true };
}

export function createEventHookPayload(values: EventHookFormValues) {
  const parsed = eventHookSchema.parse(values);
  return { name: parsed.name, url: parsed.url, applicationSystemId: parsed.applicationSystemId || null, eventTypes: normalizeTypes(parsed.eventTypes) };
}

export function updateEventHookPayload(values: EventHookFormValues, version: number) {
  const parsed = eventHookSchema.parse(values);
  return { name: parsed.name, url: parsed.url, eventTypes: normalizeTypes(parsed.eventTypes), isActive: parsed.isActive, version };
}

/** Subscribing to everything makes every other type redundant. */
export function normalizeTypes(types: readonly string[]): string[] {
  const unique = [...new Set(types.map((type) => type.trim()).filter(Boolean))];
  return unique.includes(WILDCARD) ? [WILDCARD] : unique;
}

function isHttpsUrl(value: string): boolean {
  try {
    const url = new URL(value.trim());
    return url.protocol === "https:" && Boolean(url.hostname);
  } catch {
    return false;
  }
}

/** The delivery's JSON body indented for reading, or the raw text. */
export function formatPayload(payload: string | null | undefined): string {
  if (!payload) return "";
  try {
    return JSON.stringify(JSON.parse(payload), null, 2);
  } catch {
    return payload;
  }
}

/** The receiver's check of X-AuthCenter-Signature, shown next to the secret. */
export const SIGNATURE_SNIPPET = `// Node.js: verifica X-AuthCenter-Signature antes de procesar el evento.
import { createHmac, timingSafeEqual } from "node:crypto";

function isSigned(secret, timestamp, rawBody, header) {
  const expected = createHmac("sha256", secret).update(\`\${timestamp}.\${rawBody}\`).digest("hex");
  // Durante una rotación llegan dos firmas: "v1=<nueva>,v1=<anterior>".
  return header.split(",").some((part) => {
    const value = part.trim().replace(/^v1=/, "");
    return value.length === expected.length && timingSafeEqual(Buffer.from(value), Buffer.from(expected));
  });
}`;
