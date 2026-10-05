import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { lazy, Suspense, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationBranding, ApplicationSummary, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { Pagination } from "../../components/Pagination";
import { StatusBadge } from "../../components/StatusBadge";
import { buildQuery } from "../../utils/format";
import type { BrandingFormValues } from "./branding";

const BrandingDialog = lazy(() => import("./BrandingDialog").then((module) => ({ default: module.BrandingDialog })));

export default function ApplicationsPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_APPLICATIONS_WRITE");
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get("page")) || 1);
  const pageSize = [20, 50, 100].includes(Number(params.get("pageSize"))) ? Number(params.get("pageSize")) : 20;
  const [selected, setSelected] = useState<ApplicationSummary | null>(null);
  const [feedback, setFeedback] = useState("");
  const queryClient = useQueryClient();
  const applications = useQuery({
    queryKey: ["applications", page, pageSize],
    queryFn: ({ signal }) => apiRequest<PagedResult<ApplicationSummary>>(`/api/applications?${buildQuery({ page, pageSize })}`, { signal })
  });
  const updateBranding = useMutation({
    mutationFn: ({ id, values }: { id: string; values: BrandingFormValues }) => apiRequest<ApplicationBranding>(`/api/applications/${id}/branding`, {
      method: "PUT",
      body: JSON.stringify({
        ...values,
        logoUrl: values.logoUrl || null,
        supportUrl: values.supportUrl || null,
        privacyUrl: values.privacyUrl || null,
        termsUrl: values.termsUrl || null
      })
    }),
    onSuccess: async () => {
      setFeedback("La marca quedó guardada.");
      setSelected(null);
      await queryClient.invalidateQueries({ queryKey: ["applications"] });
    }
  });

  function updatePage(name: "page" | "pageSize", value: number): void {
    setParams((current) => {
      const next = new URLSearchParams(current);
      next.set(name, String(value));
      if (name === "pageSize") next.set("page", "1");
      return next;
    });
  }

  return (
    <>
      <PageHeader eyebrow="Aplicaciones" title="Aplicaciones" description="Cada aplicación agrupa sus clientes de inicio de sesión, quién puede entrar y cómo. Empieza registrando una." actions={canWrite ? <Link className="button" to="/applications/new">Nueva aplicación</Link> : undefined} />
      {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
      {applications.isPending ? <PageState title="Cargando aplicaciones" busy /> : null}
      {applications.isError ? <PageState title="No pudimos cargar aplicaciones" detail={errorMessage(applications.error)} tone="error" action={<button className="button" type="button" onClick={() => void applications.refetch()}>Reintentar</button>} /> : null}
      {applications.data ? <><div className="application-grid">{applications.data.items.map((application) => (
        <article className="application-card" key={application.id}>
          <div className="application-card__heading"><span className="application-logo" style={{ background: application.branding?.backgroundColor ?? "#f8fafc", color: application.branding?.primaryColor ?? "#2563eb" }}>{application.branding?.logoUrl ? <img src={application.branding.logoUrl} alt="" /> : application.code.slice(0, 2)}</span><StatusBadge active={application.isActive} activeLabel="Activa" inactiveLabel="Inactiva" /></div>
          <div><p className="eyebrow">{application.code}</p><h2>{application.branding?.displayName ?? application.name}</h2><p>{application.description ?? "Sin descripción"}</p></div>
          <div className="application-card__links">{application.branding?.privacyUrl ? <span>Privacidad ✓</span> : <span>Privacidad pendiente</span>}{application.branding?.termsUrl ? <span>Términos ✓</span> : <span>Términos pendientes</span>}</div>
          <div className="application-card__actions"><Link className="button button--secondary" to={`/applications/${application.id}`}>Ver configuración</Link>{canWrite ? <button className="button button--quiet" type="button" onClick={() => { updateBranding.reset(); setSelected(application); }}>Editar marca<span className="sr-only"> de {application.name}</span></button> : null}</div>
        </article>
      ))}</div><Pagination page={applications.data.page} pageSize={applications.data.pageSize} totalCount={applications.data.totalCount} totalPages={applications.data.totalPages} onPageChange={(value) => updatePage("page", value)} onPageSizeChange={(value) => updatePage("pageSize", value)} /></> : null}
      {selected ? <Suspense fallback={<p className="alert" role="status">Cargando el editor de marca…</p>}><BrandingDialog application={selected} busy={updateBranding.isPending} error={updateBranding.error ? errorMessage(updateBranding.error) : ""} onClose={() => { if (!updateBranding.isPending) setSelected(null); }} onSave={async (values) => { await updateBranding.mutateAsync({ id: selected.id, values }); }} /></Suspense> : null}
    </>
  );
}

