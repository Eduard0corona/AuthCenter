import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { Link, useNavigate } from "react-router-dom";
import { useApplicationsCatalog } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import type { AccessReview } from "../../api/types";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { Field } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { SaveError } from "../../components/SaveError";
import { governanceMessages, reviewDefaults, reviewPayload, reviewSchema, type ReviewFormValues } from "./governance";

/** Starts an access review: its items are the accesses the application has at that moment. */
export default function AccessReviewEditorPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const applications = useApplicationsCatalog();
  const [defaults] = useState(() => reviewDefaults());
  const form = useForm<ReviewFormValues>({ resolver: zodResolver(reviewSchema), defaultValues: defaults });
  const errors = form.formState.errors;
  const save = useMutation({
    mutationFn: (values: ReviewFormValues) => apiRequest<AccessReview>("/api/governance/access-reviews", { method: "POST", body: JSON.stringify(reviewPayload(values)) }),
    onSuccess: async (review) => {
      await queryClient.invalidateQueries({ queryKey: ["access-reviews"] });
      navigate(`/access-reviews/${review.id}`, { replace: true });
    }
  });

  return <>
    <Breadcrumbs items={[{ label: "Revisiones de acceso", to: "/access-reviews" }, { label: "Nueva revisión" }]} />
    <PageHeader eyebrow="Gobierno" title="Nueva revisión de acceso" description="La revisión incluye a quien tiene acceso a la aplicación ahora, directo o por grupos. Sus responsables reciben un correo y deciden desde su portal." actions={<Link className="button button--secondary" to="/access-reviews">Volver al listado</Link>} />
    <SaveError error={save.error} messages={governanceMessages} />
    <form className="settings-form" noValidate onSubmit={(event) => void form.handleSubmit((values) => save.mutateAsync(values).catch(() => undefined))(event)}>
      <section className="settings-panel" aria-labelledby="review-definition">
        <div className="settings-panel__heading"><div><h2 id="review-definition">Campaña</h2><p>Una aplicación tiene una sola revisión en curso a la vez.</p></div></div>
        <div className="form-grid">
          <Field label="Nombre" error={errors.name?.message}><input {...form.register("name")} autoComplete="off" placeholder="Revisión trimestral de CRM" /></Field>
          <Field label="Aplicación" error={errors.applicationSystemId?.message}><select {...form.register("applicationSystemId")}><option value="">Selecciona una aplicación</option>{applications.data?.filter((application) => application.isActive).map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></Field>
          <Field label="Fecha límite" error={errors.dueAt?.message} help="Hora de tu navegador. Al llegar, la revisión se cierra."><input type="datetime-local" {...form.register("dueAt")} /></Field>
          <Field label="Repetir" error={errors.recurrenceMonths?.message} help="La siguiente campaña empieza ese tiempo después de que empezó esta."><select {...form.register("recurrenceMonths")}>
            <option value="">No repetir</option>
            <option value="1">Cada mes</option>
            <option value="3">Cada 3 meses</option>
            <option value="6">Cada 6 meses</option>
            <option value="12">Cada año</option>
          </select></Field>
        </div>
        <div className="checkbox-grid"><label className="checkbox-field"><input type="checkbox" {...form.register("revokeUnreviewed")} /><span>Revocar los accesos que nadie revise antes de la fecha límite (si no, se mantienen)</span></label></div>
      </section>
      <div className="form-footer">
        <Link className="button button--secondary" to="/access-reviews">Cancelar</Link>
        <button className="button" type="submit" disabled={save.isPending}>{save.isPending ? "Creando…" : "Iniciar revisión"}</button>
      </div>
    </form>
  </>;
}
