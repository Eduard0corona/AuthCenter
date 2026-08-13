import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { apiRequest, ApiError } from "../../api/client";
import type { ApplicationSummary, PagedResult } from "../../api/types";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { StatusBadge } from "../../components/StatusBadge";

export default function AccessPoliciesPage() {
  const applications = useQuery({
    queryKey: ["applications", "access-policies"],
    queryFn: ({ signal }) => apiRequest<PagedResult<ApplicationSummary>>("/api/applications?page=1&pageSize=100", { signal })
  });

  return <>
    <PageHeader eyebrow="Seguridad" title="Políticas de acceso" description="Administra reglas versionadas y simula decisiones antes de publicar cambios que afectan sesiones." />
    {applications.isPending ? <PageState title="Cargando aplicaciones" busy /> : null}
    {applications.isError ? <PageState title="No pudimos cargar las aplicaciones" detail={message(applications.error)} tone="error" action={<button className="button" onClick={() => void applications.refetch()}>Reintentar</button>} /> : null}
    {applications.data && applications.data.items.length === 0 ? <PageState title="No hay aplicaciones" detail="Registra una aplicación antes de definir sus políticas de acceso." /> : null}
    {applications.data?.items.length ? <div className="data-table" tabIndex={0} role="region" aria-label="Aplicaciones con políticas, desplazamiento horizontal"><table><caption className="sr-only">Aplicaciones con políticas de acceso</caption><thead><tr><th>Aplicación</th><th>Código</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{applications.data.items.map((application) => <tr key={application.id}><td><strong>{application.name}</strong><span className="cell-detail">{application.description ?? "Sin descripción"}</span></td><td><code>{application.code}</code></td><td><StatusBadge active={application.isActive} /></td><td className="table-action"><Link className="button button--small button--secondary" to={`/access-policies/${application.id}`}>Administrar política</Link></td></tr>)}</tbody></table></div> : null}
  </>;
}

function message(error: unknown): string { return error instanceof ApiError ? error.message : "Ocurrió un error inesperado."; }
