import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { AdminDashboard } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { formatDate } from "../../utils/format";

const modules = [
  { title: "Usuarios", description: "Directorio, estado y acceso efectivo.", to: "/users", permission: "AUTHCENTER_USERS_READ", accent: "blue" },
  { title: "Aplicaciones", description: "Configuración, registro y branding.", to: "/applications", permission: "AUTHCENTER_APPLICATIONS_READ", accent: "violet" },
  { title: "Roles", description: "RBAC, permisos y alcance por aplicación.", to: "/roles", permission: "AUTHCENTER_ROLES_READ", accent: "blue" },
  { title: "Permisos", description: "Catálogo de capacidades por aplicación.", to: "/permissions", permission: "AUTHCENTER_PERMISSIONS_READ", accent: "emerald" },
  { title: "System Log", description: "Eventos administrativos y trazabilidad.", to: "/system-log", permission: "AUTHCENTER_AUDIT_LOGS_READ", accent: "amber" },
  { title: "Event Hooks", description: "Suscripciones, entregas y replay controlado.", to: "/event-hooks", permission: "AUTHCENTER_EVENT_HOOKS_READ", accent: "emerald" },
  { title: "Gobierno de accesos", description: "Solicitudes, revisiones periódicas y segregación de funciones.", to: "/access-requests", permission: "AUTHCENTER_GOVERNANCE_READ", accent: "violet" }
];

type Tone = "neutral" | "attention" | "critical";

interface Metric {
  key: string;
  label: string;
  value: number;
  detail?: string;
  to?: string;
  permission?: string;
  tone: Tone;
}

export default function DashboardPage() {
  const { user, permissions } = useSession();
  const canReadMetrics = permissions.has("AUTHCENTER_AUDIT_LOGS_READ");
  const dashboard = useQuery({
    queryKey: ["admin-dashboard"],
    queryFn: ({ signal }) => apiRequest<AdminDashboard>("/api/admin-dashboard", { signal }),
    enabled: canReadMetrics,
    refetchInterval: 60_000
  });

  return (
    <>
      <PageHeader eyebrow="Overview" title={`Hola, ${user.name?.split(" ")[0] ?? "operador"}`} description="Opera AuthCenter desde módulos cargados bajo demanda. Cada acción vuelve a autorizarse en el servidor." />
      {canReadMetrics ? (
        <section className="settings-panel dashboard-panel" aria-labelledby="platform-status" aria-busy={dashboard.isFetching || undefined}>
          <div className="settings-panel__heading">
            <div><h2 id="platform-status">Estado de la plataforma</h2><p>{dashboard.data ? `Actualizado ${formatDate(dashboard.data.generatedAt)}. Se renueva cada minuto.` : "Indicadores de directorio, integraciones y seguridad."}</p></div>
            <button className="button button--small button--secondary" type="button" onClick={() => void dashboard.refetch()} disabled={dashboard.isFetching}>{dashboard.isFetching ? "Actualizando…" : "Actualizar"}</button>
          </div>
          {dashboard.isError ? <p className="alert alert--error" role="alert">{errorMessage(dashboard.error)}</p> : null}
          {dashboard.isPending ? <p className="muted">Cargando indicadores…</p> : null}
          {dashboard.data ? <ul className="metric-grid">{metrics(dashboard.data).map((metric) => {
            const content = <><span className="metric-card__value">{metric.value.toLocaleString("es-MX")}</span><span className="metric-card__label">{metric.label}</span>{metric.detail ? <span className="metric-card__detail">{metric.detail}</span> : null}</>;
            const linked = metric.to && (!metric.permission || permissions.has(metric.permission));
            return <li key={metric.key}>{linked ? <Link className={`metric-card metric-card--${metric.tone}`} to={metric.to!}>{content}</Link> : <div className={`metric-card metric-card--${metric.tone}`}>{content}</div>}</li>;
          })}</ul> : null}
        </section>
      ) : null}
      <section className="module-grid" aria-label="Módulos disponibles">
        {modules.filter((module) => permissions.has(module.permission)).map((module) => (
          <Link className={`module-card module-card--${module.accent}`} to={module.to} key={module.to}>
            <span className="module-card__icon" aria-hidden="true">↗</span>
            <h2>{module.title}</h2><p>{module.description}</p><strong>Abrir módulo</strong>
          </Link>
        ))}
      </section>
      <section className="security-note">
        <div className="security-note__mark" aria-hidden="true">✓</div>
        <div><h2>Frontera de seguridad server-side</h2><p>La consola oculta acciones no autorizadas para reducir ruido, pero ninguna decisión depende del navegador. La API valida de nuevo sesión, CSRF y permisos.</p></div>
      </section>
    </>
  );
}

function metrics(data: AdminDashboard): Metric[] {
  const attention = (value: number): Tone => (value > 0 ? "attention" : "neutral");
  return [
    { key: "users", label: "Usuarios activos", value: data.activeUsers, detail: `${data.inactiveUsers.toLocaleString("es-MX")} inactivos o eliminados`, to: "/users?active=true", permission: "AUTHCENTER_USERS_READ", tone: "neutral" },
    { key: "pending", label: "Solicitudes de acceso pendientes", value: data.pendingAccessRequests, detail: "Esperan la decisión de sus responsables", to: "/access-requests?status=Pending", permission: "AUTHCENTER_GOVERNANCE_READ", tone: attention(data.pendingAccessRequests) },
    { key: "reviews", label: "Revisiones de acceso en curso", value: data.activeAccessReviews, detail: `${data.pendingAccessReviewItems.toLocaleString("es-MX")} accesos por revisar${data.overdueAccessReviews > 0 ? ` · ${data.overdueAccessReviews.toLocaleString("es-MX")} vencidas` : ""}`, to: "/access-reviews?status=Active", permission: "AUTHCENTER_GOVERNANCE_READ", tone: data.overdueAccessReviews > 0 ? "critical" : attention(data.pendingAccessReviewItems) },
    { key: "sod", label: "Violaciones de segregación de funciones", value: data.separationOfDutiesViolations, detail: "Usuarios con roles incompatibles", to: "/sod-rules", permission: "AUTHCENTER_GOVERNANCE_READ", tone: data.separationOfDutiesViolations > 0 ? "critical" : "neutral" },
    { key: "applications", label: "Aplicaciones activas", value: data.activeApplications, to: "/applications", permission: "AUTHCENTER_APPLICATIONS_READ", tone: "neutral" },
    { key: "groups", label: "Grupos activos", value: data.activeGroups, to: "/groups", permission: "AUTHCENTER_GROUPS_READ", tone: "neutral" },
    { key: "federation", label: "Proveedores federados activos", value: data.activeFederationProviders, to: "/federation", permission: "AUTHCENTER_FEDERATION_READ", tone: "neutral" },
    { key: "tokens", label: "Provisioning tokens por vencer", value: data.expiringProvisioningTokens, detail: "En los próximos 30 días", to: "/provisioning-tokens", permission: "AUTHCENTER_PROVISIONING_READ", tone: attention(data.expiringProvisioningTokens) },
    { key: "hooks", label: "Event hooks sin verificar", value: data.unverifiedEventHooks, detail: "No reciben eventos hasta verificarse", to: "/event-hooks", permission: "AUTHCENTER_EVENT_HOOKS_READ", tone: attention(data.unverifiedEventHooks) },
    { key: "dead-letters", label: "Entregas en dead letter", value: data.deadLetterDeliveries, detail: "Agotaron sus reintentos", to: "/event-hooks/deliveries?status=dead-letter", permission: "AUTHCENTER_EVENT_HOOKS_READ", tone: data.deadLetterDeliveries > 0 ? "critical" : "neutral" },
    { key: "failed-logins", label: "Inicios de sesión rechazados", value: data.failedLoginsLast24Hours, detail: "Últimas 24 horas", to: "/system-log?action=LOGIN_FAILED", permission: "AUTHCENTER_AUDIT_LOGS_READ", tone: attention(data.failedLoginsLast24Hours) },
    { key: "risk", label: "Inicios de sesión de riesgo alto", value: data.highRiskObservationsLast24Hours, detail: "Últimas 24 horas", tone: data.highRiskObservationsLast24Hours > 0 ? "critical" : "neutral" }
  ];
}
