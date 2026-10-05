import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { useApplicationsCatalog } from "../../api/catalog";
import { ApiError, apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { EventHook, EventHookSecret, EventTypeInfo } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { Field } from "../../components/Field";
import { HistoryLink } from "../../components/HistoryLink";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { ReauthenticationDialog } from "../../components/ReauthenticationDialog";
import { StatusBadge } from "../../components/StatusBadge";
import { formatDate } from "../../utils/format";
import { SecretRevealDialog } from "../oauth-clients/SecretRevealDialog";
import { createEventHookPayload, eventHookDefaults, eventHookSchema, SIGNATURE_SNIPPET, updateEventHookPayload, type EventHookFormValues } from "./event-hook";
import { EventTypePicker } from "./EventTypePicker";

const HOOK_ERRORS: Record<string, string> = {
  INVALID_EVENT_HOOK: "Revisa el nombre, que la URL sea HTTPS y resuelva a un endpoint público, y que haya entre 1 y 100 tipos de evento.",
  EVENT_HOOK_EXISTS: "Ya existe un webhook con ese nombre en este alcance.",
  APP_NOT_FOUND: "La aplicación no existe o está inactiva.",
  EVENT_HOOK_NOT_FOUND: "El webhook no existe o está desactivado.",
  EVENT_HOOK_UNSAFE_URL: "La URL ya no resuelve a un endpoint público. Corrígela antes de verificar.",
  EVENT_HOOK_VERIFICATION_FAILED: "El endpoint no devolvió el reto de verificación. Debe responder 2xx con el valor recibido en el encabezado X-AuthCenter-Verification o en el cuerpo.",
  REAUTHENTICATION_REQUIRED: "La confirmación de identidad expiró. Vuelve a intentarlo."
};

// One page instance per hook, so a dialog or pending action never carries over to another hook.
export default function EventHookEditorRoute({ create = false }: { create?: boolean }) {
  const { hookId = "" } = useParams();
  return <EventHookEditorPage key={create ? "new" : hookId} create={create} />;
}

function EventHookEditorPage({ create }: { create: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_EVENT_HOOKS_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const { hookId = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState("");
  const [secret, setSecret] = useState<EventHookSecret | null>(null);
  const [rotating, setRotating] = useState(false);
  const hook = useQuery({
    queryKey: ["event-hook", hookId],
    enabled: !create && Boolean(hookId),
    queryFn: ({ signal }) => apiRequest<EventHook>(`/api/event-hooks/${hookId}`, { signal })
  });
  const catalog = useQuery({
    queryKey: ["event-hook-types"],
    staleTime: Number.POSITIVE_INFINITY,
    queryFn: ({ signal }) => apiRequest<EventTypeInfo[]>("/api/event-hooks/event-types", { signal })
  });
  const applications = useApplicationsCatalog(create && canReadApplications);
  const form = useForm<EventHookFormValues>({ resolver: zodResolver(eventHookSchema), defaultValues: eventHookDefaults() });
  const current = hook.data;
  const { reset } = form;
  // A reload (after verifying or rotating) refreshes the form without discarding unsaved edits.
  useEffect(() => { if (current) reset(eventHookDefaults(current), { keepDirtyValues: true }); }, [current, reset]);

  const refresh = () => Promise.all([
    queryClient.invalidateQueries({ queryKey: ["event-hook", hookId] }),
    queryClient.invalidateQueries({ queryKey: ["event-hooks"] })
  ]);
  const save = useMutation({
    mutationFn: async (values: EventHookFormValues) => {
      if (create) return { created: await apiRequest<EventHookSecret>("/api/event-hooks", { method: "POST", body: JSON.stringify(createEventHookPayload(values)) }) };
      return { updated: await apiRequest<EventHook>(`/api/event-hooks/${hookId}`, { method: "PUT", body: JSON.stringify(updateEventHookPayload(values, current?.version ?? 0)) }) };
    },
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ["event-hooks"] });
      if ("created" in result && result.created) { setSecret(result.created); return; }
      if ("updated" in result && result.updated) {
        queryClient.setQueryData(["event-hook", hookId], result.updated);
        setFeedback(result.updated.isVerified || !current?.isVerified ? "Cambios guardados." : "Cambios guardados. La URL cambió: verifica el endpoint para volver a recibir eventos.");
      }
    }
  });
  const verify = useMutation({
    mutationFn: () => apiRequest<void>(`/api/event-hooks/${hookId}/verify`, { method: "POST" }),
    onSuccess: async () => { setFeedback("Endpoint verificado. El webhook ya recibe eventos."); await refresh(); }
  });
  const rotate = useMutation({
    mutationFn: (proofToken: string) => apiRequest<EventHookSecret>(`/api/event-hooks/${hookId}/rotate-secret`, { method: "POST", headers: { "X-AuthCenter-Reauthentication": proofToken } }),
    onSuccess: async (result) => { setRotating(false); setSecret(result); await refresh(); }
  });

  if (!create && hook.isPending) return <PageState title="Cargando webhook" busy />;
  if (!create && hook.isError) return <PageState title="No pudimos cargar el webhook" detail={errorMessage(hook.error, HOOK_ERRORS)} tone="error" action={<Link className="button" to="/event-hooks">Volver</Link>} />;
  const title = create ? "Nuevo webhook" : current?.name ?? "Webhook";
  const conflict = save.error instanceof ApiError && save.error.code === "CONCURRENCY_CONFLICT";

  // Discards the local edits and shows what the server has now.
  async function reloadCurrent(): Promise<void> {
    save.reset();
    const fresh = await hook.refetch();
    if (fresh.data) reset(eventHookDefaults(fresh.data));
  }

  function closeSecret(): void {
    const createdId = secret?.id;
    setSecret(null);
    if (create && createdId) navigate(`/event-hooks/${createdId}`, { replace: true });
  }

  return <>
    <Breadcrumbs items={[{ label: "Webhooks de eventos", to: "/event-hooks" }, { label: title }]} />
    <PageHeader
      eyebrow={create ? "Alta" : current?.applicationName ?? "Toda la plataforma"}
      title={title}
      description={create ? "Define el endpoint HTTPS y los eventos que recibirá. El secreto de firma se muestra una sola vez." : canWrite ? "Edita el destino y la suscripción, verifica el endpoint y rota el secreto de firma." : "Consulta la configuración del webhook. Tu acceso es de sólo lectura."}
      actions={<>{create ? null : <><HistoryLink entityName="EventHook" entityId={hookId} /><Link className="button button--secondary" to={`/event-hooks/deliveries?hook=${hookId}`}>Ver entregas</Link></>}<Link className="button button--secondary" to="/event-hooks">Volver al listado</Link></>}
    />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    {save.error ? <div className="alert alert--error" role="alert"><p>{errorMessage(save.error, HOOK_ERRORS)}</p>{conflict ? <button className="button button--small button--secondary" type="button" onClick={() => void reloadCurrent()}>Cargar la versión actual</button> : null}</div> : null}
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => { setFeedback(""); return save.mutateAsync(values); })(event)}>
      <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="hook-destination">
          <div className="settings-panel__heading"><div><h2 id="hook-destination">Destino</h2><p>AuthCenter entrega cada evento con un POST JSON firmado, reintenta con espera exponencial y marca como fallido lo que no logra entregar.</p></div>{current ? <StatusBadge active={current.isActive} activeLabel="Activo" inactiveLabel="Desactivado" /> : null}</div>
          <div className="form-grid">
            <Field label="Nombre" error={form.formState.errors.name?.message}><input {...form.register("name")} autoComplete="off" placeholder="SIEM corporativo" /></Field>
            <Field label="URL del endpoint" error={form.formState.errors.url?.message} help="HTTPS público. AuthCenter rechaza direcciones privadas y no sigue redirecciones."><input {...form.register("url")} type="url" inputMode="url" autoComplete="off" spellCheck={false} placeholder="https://hooks.example.com/authcenter" /></Field>
            {create ? <Field label="Alcance" help={canReadApplications ? "Un webhook de aplicación recibe sólo los eventos de esa aplicación." : "Necesitas AUTHCENTER_APPLICATIONS_READ para limitar el hook a una aplicación."}><select {...form.register("applicationSystemId")}><option value="">Toda la plataforma</option>{applications.data?.filter((application) => application.isActive).map((application) => <option key={application.id} value={application.id}>{application.name} ({application.code})</option>)}</select></Field>
              : <Field label="Alcance" help="El alcance se fija al crear el webhook."><input value={current?.applicationName ?? "Toda la plataforma"} readOnly /></Field>}
            {!create ? <label className="checkbox-field"><input type="checkbox" {...form.register("isActive")} /><span>Webhook activo: recibe eventos nuevos</span></label> : null}
          </div>
        </section>
        <section className="settings-panel" aria-labelledby="hook-events">
          <div className="settings-panel__heading"><div><h2 id="hook-events">Eventos</h2><p>Suscríbete sólo a lo que el receptor procesa. Los tipos corresponden a las acciones del registro de actividad.</p></div></div>
          {catalog.isError ? <p className="alert alert--error" role="alert">{errorMessage(catalog.error)}</p> : null}
          {catalog.isPending ? <p className="muted">Cargando el catálogo de eventos…</p> : null}
          {catalog.data ? <Controller control={form.control} name="eventTypes" render={({ field, fieldState }) => <EventTypePicker catalog={catalog.data} value={field.value} onChange={field.onChange} disabled={!canWrite} error={fieldState.error?.message} />} /> : null}
        </section>
      </fieldset>
      {canWrite ? <div className="form-footer"><Link className="button button--secondary" to="/event-hooks">Cancelar</Link><button className="button" type="submit" disabled={save.isPending || !catalog.data}>{save.isPending ? "Guardando…" : create ? "Crear webhook" : "Guardar cambios"}</button></div> : null}
    </form>
    {current ? <>
      <section className="settings-panel settings-panel--actions" aria-labelledby="hook-verification">
        <div className="settings-panel__heading"><div><h2 id="hook-verification">Verificación del endpoint</h2><p>Mientras no esté verificado, el webhook no recibe eventos. Cambiar la URL exige verificarlo de nuevo.</p></div>{current.isVerified ? <span className="tag tag--direct">Verificado {formatDate(current.verifiedAt)}</span> : <span className="tag tag--warning">Sin verificar</span>}</div>
        <p>AuthCenter envía <code>GET</code> a la URL con <code>?verification_challenge=&lt;reto&gt;</code> y el encabezado <code>X-AuthCenter-Verification</code>. El endpoint debe responder 2xx devolviendo el mismo reto en ese encabezado o en el cuerpo.</p>
        {verify.error ? <p className="alert alert--error" role="alert">{errorMessage(verify.error, HOOK_ERRORS)}</p> : null}
        {canWrite ? <div className="button-row"><button className="button button--secondary" type="button" onClick={() => { setFeedback(""); verify.mutate(); }} disabled={verify.isPending || !current.isActive}>{verify.isPending ? "Verificando…" : current.isVerified ? "Verificar de nuevo" : "Verificar endpoint"}</button>{!current.isActive ? <span className="muted">Activa el webhook para verificarlo.</span> : null}</div> : null}
      </section>
      <section className="settings-panel settings-panel--actions" aria-labelledby="hook-secret">
        <div className="settings-panel__heading"><div><h2 id="hook-secret">Secreto de firma</h2><p>Cada entrega lleva <code>X-AuthCenter-Timestamp</code> y <code>X-AuthCenter-Signature: v1=&lt;HMAC-SHA256 de "timestamp.cuerpo"&gt;</code>.</p></div></div>
        {current.previousSecretExpiresAt ? <p className="alert alert--info">Rotación en curso: hasta el {formatDate(current.previousSecretExpiresAt)} las entregas se firman con el secreto nuevo y con el anterior, para que el receptor se actualice sin cortes.</p> : null}
        <details className="snippet"><summary>Cómo verificar la firma en el receptor</summary><pre className="mono code-block">{SIGNATURE_SNIPPET}</pre></details>
        {rotate.error ? <p className="alert alert--error" role="alert">{errorMessage(rotate.error, HOOK_ERRORS)}</p> : null}
        {canWrite ? <div className="button-row"><button className="button button--danger-quiet" type="button" onClick={() => { setFeedback(""); rotate.reset(); setRotating(true); }}>Rotar secreto</button></div> : null}
      </section>
    </> : null}
    <ReauthenticationDialog open={rotating} purpose="admin.event-hook.rotate-secret" title="Rotar el secreto de firma" detail="Se generará un secreto nuevo que verás una sola vez. Durante 24 horas las entregas también se firmarán con el anterior." confirmLabel="Verificar y rotar" onCancel={() => setRotating(false)} onProof={async (proofToken) => { await rotate.mutateAsync(proofToken); }} />
    <SecretRevealDialog open={secret !== null} secret={secret?.secret ?? ""} title={create ? `Secreto de ${form.getValues("name") || "el webhook"}` : `Nuevo secreto de ${title}`} onClose={closeSecret} />
  </>;
}
