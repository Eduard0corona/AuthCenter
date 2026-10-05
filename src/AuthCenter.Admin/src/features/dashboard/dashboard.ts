import type { AdminDashboard } from "../../api/types";

export type Tone = "neutral" | "attention" | "critical";

export interface Metric {
  key: string;
  label: string;
  value: number;
  detail?: string;
  to?: string;
  permission?: string;
  tone: Tone;
}

export interface QuickAction {
  label: string;
  to: string;
  /** Every permission the destination needs to work, not only to open. */
  permissions: string[];
}

// The most common first tasks. Creating a client or inviting someone also needs to read the
// applications (to choose one), so the action is offered only when the page can be completed.
const QUICK_ACTIONS: QuickAction[] = [
  { label: "Registrar aplicación", to: "/applications/new", permissions: ["AUTHCENTER_APPLICATIONS_WRITE"] },
  { label: "Crear cliente OAuth", to: "/oauth-clients/new", permissions: ["AUTHCENTER_OAUTH_CLIENTS_WRITE", "AUTHCENTER_APPLICATIONS_READ"] },
  { label: "Invitar usuario", to: "/users/invite", permissions: ["AUTHCENTER_USERS_WRITE", "AUTHCENTER_APPLICATIONS_READ"] }
];

/** The quick actions the operator can complete. */
export function quickActions(granted: ReadonlySet<string>): QuickAction[] {
  return QUICK_ACTIONS.filter((action) => action.permissions.every((permission) => granted.has(permission)));
}

/** The platform indicators, in their reading order. */
export function dashboardMetrics(data: AdminDashboard): Metric[] {
  const attention = (value: number): Tone => (value > 0 ? "attention" : "neutral");
  const count = (value: number) => value.toLocaleString("es-MX");
  return [
    { key: "users", label: "Usuarios activos", value: data.activeUsers, detail: `${count(data.inactiveUsers)} inactivos o eliminados`, to: "/users?active=true", permission: "AUTHCENTER_USERS_READ", tone: "neutral" },
    { key: "pending", label: "Solicitudes de acceso pendientes", value: data.pendingAccessRequests, detail: "Esperan la decisión de sus responsables", to: "/access-requests?status=Pending", permission: "AUTHCENTER_GOVERNANCE_READ", tone: attention(data.pendingAccessRequests) },
    {
      key: "reviews",
      label: "Revisiones de acceso en curso",
      value: data.activeAccessReviews,
      detail: `${count(data.pendingAccessReviewItems)} accesos por revisar${data.overdueAccessReviews > 0 ? ` · ${count(data.overdueAccessReviews)} vencidas` : ""}`,
      to: "/access-reviews?status=Active",
      permission: "AUTHCENTER_GOVERNANCE_READ",
      tone: data.overdueAccessReviews > 0 ? "critical" : attention(data.pendingAccessReviewItems)
    },
    { key: "sod", label: "Violaciones de segregación de funciones", value: data.separationOfDutiesViolations, detail: "Usuarios con roles incompatibles", to: "/sod-rules", permission: "AUTHCENTER_GOVERNANCE_READ", tone: data.separationOfDutiesViolations > 0 ? "critical" : "neutral" },
    { key: "applications", label: "Aplicaciones activas", value: data.activeApplications, to: "/applications", permission: "AUTHCENTER_APPLICATIONS_READ", tone: "neutral" },
    { key: "groups", label: "Grupos activos", value: data.activeGroups, to: "/groups", permission: "AUTHCENTER_GROUPS_READ", tone: "neutral" },
    { key: "federation", label: "Proveedores federados activos", value: data.activeFederationProviders, to: "/federation", permission: "AUTHCENTER_FEDERATION_READ", tone: "neutral" },
    { key: "tokens", label: "Tokens de aprovisionamiento por vencer", value: data.expiringProvisioningTokens, detail: "En los próximos 30 días", to: "/provisioning-tokens", permission: "AUTHCENTER_PROVISIONING_READ", tone: attention(data.expiringProvisioningTokens) },
    { key: "hooks", label: "Webhooks sin verificar", value: data.unverifiedEventHooks, detail: "No reciben eventos hasta verificarse", to: "/event-hooks", permission: "AUTHCENTER_EVENT_HOOKS_READ", tone: attention(data.unverifiedEventHooks) },
    { key: "dead-letters", label: "Entregas fallidas", value: data.deadLetterDeliveries, detail: "Agotaron sus reintentos", to: "/event-hooks/deliveries?status=dead-letter", permission: "AUTHCENTER_EVENT_HOOKS_READ", tone: data.deadLetterDeliveries > 0 ? "critical" : "neutral" },
    { key: "failed-logins", label: "Inicios de sesión rechazados", value: data.failedLoginsLast24Hours, detail: "Últimas 24 horas", to: "/system-log?action=LOGIN_FAILED", permission: "AUTHCENTER_AUDIT_LOGS_READ", tone: attention(data.failedLoginsLast24Hours) },
    { key: "risk", label: "Inicios de sesión de riesgo alto", value: data.highRiskObservationsLast24Hours, detail: "Últimas 24 horas", tone: data.highRiskObservationsLast24Hours > 0 ? "critical" : "neutral" }
  ];
}

/** What needs attention goes first (critical, then attention); the rest keeps its order. */
export function byAttention(metrics: Metric[]): Metric[] {
  const rank = (metric: Metric) => (metric.value <= 0 ? 2 : metric.tone === "critical" ? 0 : metric.tone === "attention" ? 1 : 2);
  return [...metrics].sort((first, second) => rank(first) - rank(second));
}
