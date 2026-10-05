import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { AdminDashboard } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { formatDate } from "../../utils/format";
import { byAttention, dashboardMetrics, quickActions, type Metric } from "./dashboard";

export default function DashboardPage() {
  const { user, permissions } = useSession();
  const canReadMetrics = permissions.has("AUTHCENTER_AUDIT_LOGS_READ");
  const actions = quickActions(permissions);
  const dashboard = useQuery({
    queryKey: ["admin-dashboard"],
    queryFn: ({ signal }) => apiRequest<AdminDashboard>("/api/admin-dashboard", { signal }),
    enabled: canReadMetrics,
    refetchInterval: 60_000
  });

  return (
    <>
      <PageHeader
        eyebrow="Inicio"
        title={`Hola, ${user.name?.split(" ")[0] ?? "operador"}`}
        documentTitle="Inicio"
        description="Revisa lo que necesita atención y empieza las tareas más comunes."
      />
      {actions.length > 0 ? (
        <section className="quick-actions" aria-labelledby="quick-actions">
          <h2 id="quick-actions">Acciones rápidas</h2>
          <ul className="quick-actions__list">
            {actions.map((action, index) => (
              <li key={action.to}><Link className={index === 0 ? "button" : "button button--secondary"} to={action.to}>{action.label}</Link></li>
            ))}
          </ul>
        </section>
      ) : null}
      {canReadMetrics ? (
        <section className="settings-panel dashboard-panel" aria-labelledby="platform-status" aria-busy={dashboard.isFetching || undefined}>
          <div className="settings-panel__heading">
            <div>
              <h2 id="platform-status">Estado de la plataforma</h2>
              <p>{dashboard.data ? `Lo que requiere atención aparece primero. Actualizado ${formatDate(dashboard.data.generatedAt)}; se renueva cada minuto.` : "Indicadores de directorio, integraciones y seguridad."}</p>
            </div>
            <button className="button button--small button--secondary" type="button" onClick={() => void dashboard.refetch()} disabled={dashboard.isFetching}>
              {dashboard.isFetching ? "Actualizando…" : "Actualizar"}
            </button>
          </div>
          {dashboard.isError ? <p className="alert alert--error" role="alert">{errorMessage(dashboard.error)}</p> : null}
          {dashboard.isPending ? <p className="muted">Cargando indicadores…</p> : null}
          {dashboard.data ? (
            <ul className="metric-grid">
              {byAttention(dashboardMetrics(dashboard.data)).map((metric) => <li key={metric.key}><MetricCard metric={metric} permissions={permissions} /></li>)}
            </ul>
          ) : null}
        </section>
      ) : null}
    </>
  );
}

function MetricCard({ metric, permissions }: { metric: Metric; permissions: ReadonlySet<string> }) {
  const content = <>
    <span className="metric-card__value">{metric.value.toLocaleString("es-MX")}</span>
    <span className="metric-card__label">{metric.label}</span>
    {metric.detail ? <span className="metric-card__detail">{metric.detail}</span> : null}
  </>;
  const className = `metric-card metric-card--${metric.tone}`;
  if (metric.to && (!metric.permission || permissions.has(metric.permission))) return <Link className={className} to={metric.to}>{content}</Link>;
  return <div className={className}>{content}</div>;
}
