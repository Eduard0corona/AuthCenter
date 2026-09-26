import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type ReactNode } from "react";
import { useForm } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest, ApiError } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, ProfileAttributeDefinition, ProfileMapping, ProfileMappingSimulation } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { HistoryLink } from "../../components/HistoryLink";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { StatusBadge } from "../../components/StatusBadge";
import { formatDate } from "../../utils/format";
import { formatSimulatedValue, parseSourceDocument, profileMappingCreatePayload, profileMappingDefaults, profileMappingFromResponse, profileMappingSchema, profileMappingUpdatePayload, sampleSourceDocument, type ProfileMappingFormValues } from "./profile-mapping";

export default function ProfileMappingEditorPage({ create = false }: { create?: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_USERS_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const canReadSchema = permissions.has("AUTHCENTER_PROFILE_SCHEMAS_READ");
  const { mappingId = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState("");
  const [validation, setValidation] = useState("");
  const [sourceDocument, setSourceDocument] = useState(sampleSourceDocument);
  const [documentError, setDocumentError] = useState("");
  const [confirmDelete, setConfirmDelete] = useState(false);
  const mapping = useQuery({
    queryKey: ["profile-mapping", mappingId],
    enabled: !create && Boolean(mappingId),
    queryFn: ({ signal }) => apiRequest<ProfileMapping>(`/api/lifecycle/profile-mappings/${mappingId}`, { signal })
  });
  const applications = useQuery({
    queryKey: ["applications", "profile-mapping-editor"],
    enabled: create && canReadApplications,
    queryFn: ({ signal }) => fetchAllAsPage<ApplicationSummary>("/api/applications", signal)
  });
  const schema = useQuery({
    queryKey: ["profile-schema", "active"],
    enabled: canReadSchema,
    queryFn: ({ signal }) => apiRequest<ProfileAttributeDefinition[]>("/api/profile-schema", { signal })
  });
  const form = useForm<ProfileMappingFormValues>({ resolver: zodResolver(profileMappingSchema), defaultValues: profileMappingDefaults() });
  const current = mapping.data;
  const { reset } = form;
  useEffect(() => { if (current) reset(profileMappingFromResponse(current)); }, [current, reset]);

  const save = useMutation({
    mutationFn: (values: ProfileMappingFormValues) => create
      ? apiRequest<ProfileMapping>("/api/lifecycle/profile-mappings", { method: "POST", body: JSON.stringify(profileMappingCreatePayload(values)) })
      : apiRequest<ProfileMapping>(`/api/lifecycle/profile-mappings/${mappingId}`, { method: "PUT", body: JSON.stringify(profileMappingUpdatePayload(values, current?.version ?? 0)) }),
    onSuccess: async (result) => {
      setValidation("");
      await queryClient.invalidateQueries({ queryKey: ["profile-mappings"] });
      if (create) { navigate(`/profile-mappings/${result.id}`, { replace: true }); return; }
      queryClient.setQueryData(["profile-mapping", mappingId], result);
      setFeedback(`El mapping quedó guardado como versión ${result.version}.`);
    }
  });
  const validate = useMutation({
    mutationFn: (values: ProfileMappingFormValues) => apiRequest<void>("/api/lifecycle/profile-mappings/validate", { method: "POST", body: JSON.stringify(profileMappingCreatePayload(values)) }),
    onSuccess: () => setValidation("La aplicación, la ruta SCIM y el atributo destino son válidos.")
  });
  const simulate = useMutation({
    mutationFn: (document: Record<string, unknown>) => apiRequest<ProfileMappingSimulation>(`/api/lifecycle/profile-mappings/${mappingId}/simulate`, { method: "POST", body: JSON.stringify({ sourceDocument: document }) })
  });
  const remove = useMutation({
    mutationFn: () => apiRequest<void>(`/api/lifecycle/profile-mappings/${mappingId}`, { method: "DELETE" }),
    onSuccess: async () => {
      setConfirmDelete(false);
      await queryClient.invalidateQueries({ queryKey: ["profile-mappings"] });
      navigate("/profile-mappings", { replace: true });
    }
  });

  if (!create && mapping.isPending) return <PageState title="Cargando profile mapping" busy />;
  if (!create && mapping.isError) return <PageState title="No pudimos cargar el profile mapping" detail={errorMessage(mapping.error)} tone="error" action={<Link className="button" to="/profile-mappings">Volver</Link>} />;
  if (!canReadSchema) return <PageState title="No puedes administrar profile mappings" detail="Necesitas AUTHCENTER_PROFILE_SCHEMAS_READ para seleccionar el atributo destino del perfil universal." tone="forbidden" action={<Link className="button" to="/profile-mappings">Volver</Link>} />;
  if (create && !canReadApplications) return <PageState title="No puedes crear profile mappings" detail="Necesitas AUTHCENTER_APPLICATIONS_READ para seleccionar la aplicación de origen." tone="forbidden" action={<Link className="button" to="/profile-mappings">Volver</Link>} />;
  const title = create ? "Nuevo profile mapping" : current ? `${current.sourcePath} → ${current.targetAttributeName}` : "Profile mapping";
  const conflict = save.error instanceof ApiError && save.error.code === "CONCURRENCY_CONFLICT";
  const formError = validate.error ?? (conflict ? null : save.error);

  function runSimulation(): void {
    const parsed = parseSourceDocument(sourceDocument);
    if (!parsed.ok) { setDocumentError(parsed.error); return; }
    setDocumentError("");
    simulate.mutate(parsed.document);
  }

  return <>
    <Breadcrumbs items={[{ label: "Profile mappings", to: "/profile-mappings" }, { label: title }]} />
    <PageHeader eyebrow={create ? "Alta" : current?.applicationName ?? "Lifecycle"} title={title} description={create ? "Vincula una ruta del documento SCIM con un atributo activo del perfil universal." : canWrite ? "Ajusta la ruta, el atributo destino o la autoridad del mapping y verifica su efecto con una simulación." : "Consulta el mapping y simula la transformación. No tienes permisos de escritura."} actions={<>{create ? null : <HistoryLink entityName="ProfileMapping" entityId={mappingId} />}<Link className="button button--secondary" to="/profile-mappings">Volver al listado</Link></>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    {conflict ? <p className="alert alert--error" role="alert">El mapping cambió desde que lo cargaste. Recarga para ver la versión vigente antes de volver a guardar. <button className="button button--small button--secondary" type="button" onClick={() => { save.reset(); void mapping.refetch(); }}>Recargar</button></p> : null}
    {formError ? <p className="alert alert--error" role="alert">{errorMessage(formError)}</p> : null}
    {validation ? <p className="alert alert--info" role="status">{validation}</p> : null}
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => save.mutateAsync(values).catch(() => undefined))(event)}>
      <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="profile-mapping-definition">
          <div className="settings-panel__heading"><div><h2 id="profile-mapping-definition">Definición</h2><p>El origen siempre es SCIM. Las rutas usan propiedades separadas por punto y admiten extensiones URN.</p></div>{current ? <StatusBadge active={current.isActive} /> : null}</div>
          <div className="form-grid">
            {create ? <Field label="Aplicación" error={form.formState.errors.applicationSystemId?.message}><select {...form.register("applicationSystemId")}><option value="">Selecciona una aplicación</option>{applications.data?.items.filter((application) => application.isActive).map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></Field>
              : <Field label="Aplicación" error={undefined} help="La aplicación de un mapping no se puede cambiar; crea uno nuevo si es necesario."><input value={current?.applicationName ?? ""} readOnly /></Field>}
            <Field label="Ruta de origen SCIM" error={form.formState.errors.sourcePath?.message} help="Una ruta SCIM, por ejemplo name.givenName, emails[type eq &quot;work&quot;].value o urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department"><input {...form.register("sourcePath")} className="mono" autoComplete="off" spellCheck={false} placeholder="name.givenName" /></Field>
            <Field label="Atributo destino" error={form.formState.errors.targetAttributeDefinitionId?.message}><select {...form.register("targetAttributeDefinitionId")}><option value="">Selecciona un atributo</option>{schema.data?.filter((definition) => definition.isActive).map((definition) => <option key={definition.id} value={definition.id}>{definition.displayName} ({definition.key})</option>)}</select></Field>
          </div>
          <div className="checkbox-grid">
            <label className="checkbox-field"><input type="checkbox" {...form.register("isAuthoritative")} /><span>Autoritativo: el valor SCIM sobrescribe ediciones manuales</span></label>
            {create ? null : <label className="checkbox-field"><input type="checkbox" {...form.register("isActive")} /><span>Mapping activo</span></label>}
          </div>
          {current ? <dl className="profile-summary"><div><dt>Creado</dt><dd>{formatDate(current.createdAt)}</dd></div><div><dt>Versión</dt><dd>{current.version}</dd></div><div><dt>Origen</dt><dd>{current.sourceSystem}</dd></div><div><dt>Identificador</dt><dd className="mono">{current.id}</dd></div></dl> : null}
        </section>
      </fieldset>
      <div className="form-footer">
        <Link className="button button--secondary" to="/profile-mappings">Cancelar</Link>
        <button className="button button--secondary" type="button" disabled={validate.isPending} onClick={() => { validate.reset(); setValidation(""); void form.handleSubmit((values) => validate.mutateAsync(values).catch(() => undefined))(); }}>{validate.isPending ? "Validando…" : "Validar"}</button>
        {canWrite ? <button className="button" type="submit" disabled={save.isPending}>{save.isPending ? "Guardando…" : create ? "Crear mapping" : "Guardar cambios"}</button> : null}
      </div>
    </form>
    {current ? <section className="settings-panel settings-panel--actions" aria-labelledby="profile-mapping-simulation">
      <div className="settings-panel__heading"><div><h2 id="profile-mapping-simulation">Simulación</h2><p>Pega un documento SCIM de ejemplo y comprueba qué valor recibiría <span className="mono">{current.targetAttributeName}</span>. No se persiste nada.</p></div></div>
      <Field label="Documento SCIM de prueba" error={documentError}><textarea className="mono" rows={10} value={sourceDocument} spellCheck={false} onChange={(event) => setSourceDocument(event.target.value)} /></Field>
      <div className="button-group"><button className="button button--secondary" type="button" disabled={simulate.isPending} onClick={runSimulation}>{simulate.isPending ? "Simulando…" : "Simular transformación"}</button></div>
      {simulate.error ? <p className="alert alert--error" role="alert">{errorMessage(simulate.error)}</p> : null}
      {simulate.data ? <div role="status" aria-live="polite" className={`alert ${simulate.data.isValid ? "alert--success" : "alert--error"}`}>
        {simulate.data.isValid ? <><p><strong className="mono">{simulate.data.sourcePath}</strong> → <strong className="mono">{simulate.data.targetAttributeName}</strong></p><pre className="mono">{formatSimulatedValue(simulate.data.value)}</pre></> : <><p>La ruta <span className="mono">{simulate.data.sourcePath}</span> no produjo ningún valor.</p><ul>{simulate.data.errors.map((error) => <li key={error}>{error}</li>)}</ul></>}
      </div> : null}
    </section> : null}
    {current && canWrite ? <section className="settings-panel settings-panel--actions" aria-labelledby="profile-mapping-danger">
      <div className="settings-panel__heading"><div><h2 id="profile-mapping-danger">Eliminar mapping</h2><p>Las sincronizaciones futuras dejarán de poblar el atributo. Los valores ya escritos se conservan.</p></div></div>
      {remove.error ? <p className="alert alert--error" role="alert">{errorMessage(remove.error)}</p> : null}
      <div className="button-group"><button className="button button--danger-quiet" type="button" onClick={() => { remove.reset(); setConfirmDelete(true); }}>Eliminar mapping</button></div>
    </section> : null}
    <ConfirmDialog open={confirmDelete} title="Eliminar profile mapping" detail={`Se eliminará el mapping ${current?.sourcePath ?? ""} → ${current?.targetAttributeName ?? ""}. Esta acción queda auditada y no se puede deshacer.`} confirmLabel="Eliminar" dangerous busy={remove.isPending} error={remove.error} onCancel={() => setConfirmDelete(false)} onConfirm={() => remove.mutate()} />
  </>;
}

function Field({ label, error, help, children }: { label: string; error: string | undefined; help?: string; children: ReactNode }) { return <label className="field"><span>{label}</span>{children}{help ? <span className="field-help">{help}</span> : null}{error ? <span className="field-error">{error}</span> : null}</label>; }
