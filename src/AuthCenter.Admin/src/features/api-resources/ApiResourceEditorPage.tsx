import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useFieldArray, useForm } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { useApplicationsCatalog } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApiResource } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { Field } from "../../components/Field";
import { HistoryLink } from "../../components/HistoryLink";
import { SaveError } from "../../components/SaveError";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { StatusBadge } from "../../components/StatusBadge";
import { apiResourceDefaults, apiResourceSchema, createApiResourcePayload, newApiResourceSchema, removedScopes, updateApiResourcePayload, type ApiResourceFormValues } from "./api-resource";

const RESOURCE_ERRORS: Record<string, string> = {
  APPLICATION_NOT_FOUND: "La aplicación no existe o está inactiva.",
  API_IDENTIFIER_TAKEN: "Otro API ya usa ese identificador.",
  NOT_FOUND: "El API no existe."
};

export default function ApiResourceEditorRoute({ create = false }: { create?: boolean }) {
  const { resourceId = "" } = useParams();
  return <ApiResourceEditorPage key={create ? "new" : resourceId} create={create} />;
}

function ApiResourceEditorPage({ create }: { create: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_OAUTH_CLIENTS_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const { resourceId = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState("");
  const [pending, setPending] = useState<ApiResourceFormValues | null>(null);
  const resource = useQuery({
    queryKey: ["api-resource", resourceId],
    enabled: !create && Boolean(resourceId),
    queryFn: ({ signal }) => apiRequest<ApiResource>(`/api/api-resources/${resourceId}`, { signal })
  });
  const applications = useApplicationsCatalog(create && canReadApplications);
  const form = useForm<ApiResourceFormValues>({ resolver: zodResolver(create ? newApiResourceSchema : apiResourceSchema), defaultValues: apiResourceDefaults() });
  const scopes = useFieldArray({ control: form.control, name: "scopes" });
  const current = resource.data;
  const { reset } = form;
  useEffect(() => { if (current) reset(apiResourceDefaults(current)); }, [current, reset]);

  const save = useMutation({
    mutationFn: (values: ApiResourceFormValues) => create
      ? apiRequest<ApiResource>("/api/api-resources", { method: "POST", body: JSON.stringify(createApiResourcePayload(values)) })
      : apiRequest<ApiResource>(`/api/api-resources/${resourceId}`, { method: "PUT", body: JSON.stringify({ ...updateApiResourcePayload(values), version: current?.version }) }),
    onSuccess: async (saved) => {
      setPending(null);
      await queryClient.invalidateQueries({ queryKey: ["api-resources"] });
      if (create) { navigate(`/api-resources/${saved.id}`, { replace: true }); return; }
      queryClient.setQueryData(["api-resource", resourceId], saved);
      setFeedback("API guardado.");
    }
  });

  if (!create && resource.isPending) return <PageState title="Cargando API" busy />;
  if (!create && resource.isError) return <PageState title="No pudimos cargar el API" detail={errorMessage(resource.error, RESOURCE_ERRORS)} tone="error" action={<Link className="button" to="/api-resources">Volver</Link>} />;
  if (create && !canReadApplications) return <PageState title="No puedes registrar APIs" detail="Necesitas AUTHCENTER_APPLICATIONS_READ para elegir la aplicación dueña del API." tone="error" action={<Link className="button" to="/api-resources">Volver</Link>} />;
  const title = create ? "Nuevo API" : current?.displayName ?? "API";
  const errors = form.formState.errors;

  // Removing a scope breaks the clients that request it: confirm first.
  function submit(values: ApiResourceFormValues): Promise<unknown> | undefined {
    setFeedback("");
    if (removedScopes(current, values).length > 0) { setPending(values); return undefined; }
    return save.mutateAsync(values);
  }

  return <>
    <Breadcrumbs items={[{ label: "Recursos de API", to: "/api-resources" }, { label: title }]} />
    <PageHeader
      eyebrow={create ? "Alta" : current?.applicationName ?? "Aplicaciones"}
      title={title}
      description={create ? "El identificador es la audiencia (aud) de los tokens y no cambia después de crearlo." : canWrite ? "Edita la descripción y el catálogo de scopes. Los permisos del token salen de la aplicación dueña." : "Consulta el API y sus scopes. Tu acceso es de sólo lectura."}
      actions={<>{create ? null : <HistoryLink entityName="ApiResource" entityId={resourceId} />}<Link className="button button--secondary" to="/api-resources">Volver al listado</Link></>}
    />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    <SaveError error={save.error} messages={RESOURCE_ERRORS} onReload={() => { save.reset(); void resource.refetch().then((fresh) => { if (fresh.data) reset(apiResourceDefaults(fresh.data)); }); }} />
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit(submit)(event)}>
      <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="api-identity">
          <div className="settings-panel__heading"><div><h2 id="api-identity">Identidad</h2><p>Los clientes piden el API con <code>resource=&lt;identificador&gt;</code> y reciben un access token con esa audiencia.</p></div>{current ? <StatusBadge active={current.isActive} /> : null}</div>
          <div className="form-grid">
            {create
              ? <Field label="Aplicación dueña" error={errors.applicationSystemId?.message} help="Sus roles y permisos van en los tokens de este API."><select {...form.register("applicationSystemId")}><option value="">Selecciona una aplicación</option>{applications.data?.filter((application) => application.isActive).map((application) => <option key={application.id} value={application.id}>{application.name} ({application.code})</option>)}</select></Field>
              : <Field label="Aplicación dueña"><input value={`${current?.applicationName ?? ""} (${current?.applicationCode ?? ""})`} readOnly /></Field>}
            <Field label="Identificador (audiencia)" error={errors.identifier?.message} help={create ? "URI https: o urn: sin fragmento. Ej.: https://api.example.com/orders" : undefined}><input {...form.register("identifier")} readOnly={!create} className="mono" spellCheck={false} autoComplete="off" placeholder="https://api.example.com/orders" /></Field>
            <Field label="Nombre" error={errors.displayName?.message}><input {...form.register("displayName")} autoComplete="off" placeholder="Orders API" /></Field>
            <Field label="Descripción" error={errors.description?.message}><input {...form.register("description")} autoComplete="off" /></Field>
            {!create ? <label className="checkbox-field"><input type="checkbox" {...form.register("isActive")} /><span>API activo: se emiten tokens para él</span></label> : null}
          </div>
        </section>
        <section className="settings-panel" aria-labelledby="api-scopes">
          <div className="settings-panel__heading"><div><h2 id="api-scopes">Scopes</h2><p>Los nombres son únicos entre todos los APIs. Los clientes deben tenerlos en sus scopes permitidos para pedirlos.</p></div>{canWrite ? <button className="button button--small button--secondary" type="button" onClick={() => scopes.append({ name: "", displayName: "", description: "" })} disabled={scopes.fields.length >= 100}>Agregar scope</button> : null}</div>
          {errors.scopes?.message ? <p className="field-error">{errors.scopes.message}</p> : null}
          <ol className="scope-list">
            {scopes.fields.map((field, index) => <li className="scope-row" key={field.id}>
              <Field label={<>Nombre del scope<span className="sr-only"> {index + 1}</span></>} error={errors.scopes?.[index]?.name?.message}><input {...form.register(`scopes.${index}.name`)} className="mono" spellCheck={false} autoComplete="off" placeholder="orders.read" /></Field>
              <Field label={<>Nombre visible<span className="sr-only"> del scope {index + 1}</span></>} error={errors.scopes?.[index]?.displayName?.message}><input {...form.register(`scopes.${index}.displayName`)} autoComplete="off" placeholder="Leer pedidos" /></Field>
              <Field label={<>Descripción<span className="sr-only"> del scope {index + 1}</span></>} error={errors.scopes?.[index]?.description?.message}><input {...form.register(`scopes.${index}.description`)} autoComplete="off" /></Field>
              {canWrite ? <button className="button button--small button--danger-quiet" type="button" onClick={() => scopes.remove(index)} disabled={scopes.fields.length === 1}>Quitar<span className="sr-only"> el scope {index + 1}</span></button> : null}
            </li>)}
          </ol>
        </section>
      </fieldset>
      {canWrite ? <div className="form-footer"><Link className="button button--secondary" to="/api-resources">Cancelar</Link><button className="button" type="submit" disabled={save.isPending}>{save.isPending ? "Guardando…" : create ? "Crear API" : "Guardar cambios"}</button></div> : null}
    </form>
    <ConfirmDialog open={pending !== null} title="Quitar scopes" detail={pending ? `Se eliminarán ${removedScopes(current, pending).join(", ")}. Los clientes que los pidan ya no obtendrán tokens con esos scopes.` : ""} confirmLabel="Guardar y quitar" dangerous busy={save.isPending} onCancel={() => setPending(null)} onConfirm={() => { if (pending) save.mutate(pending); }} />
  </>;
}
