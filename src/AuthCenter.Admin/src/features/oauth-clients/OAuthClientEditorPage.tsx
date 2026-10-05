import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type ReactNode } from "react";
import { Controller, useForm, useWatch, type UseFormRegisterReturn } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, OAuthClientCreated, OAuthClientSecret, OAuthClientSummary } from "../../api/types";
import { askForPermission, needPermission } from "../../auth/permissions";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { HistoryLink } from "../../components/HistoryLink";
import { SaveError } from "../../components/SaveError";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { ReauthenticationDialog } from "../../components/ReauthenticationDialog";
import { StatusBadge } from "../../components/StatusBadge";
import { grantLabels, oauthClientDefaults, oauthClientPayload, oauthClientSchema, oauthGrants, oauthScopes, scopeLabels, tokenExchangeGrant, type OAuthClientFormValues } from "./oauth-client";
import { ApiScopePicker } from "./ApiScopePicker";
import { SecretRevealDialog } from "./SecretRevealDialog";

type SensitiveAction = "rotate" | "deactivate" | "activate" | null;

export default function OAuthClientEditorPage({ create = false }: { create?: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_OAUTH_CLIENTS_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const { clientId } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState("");
  const [secret, setSecret] = useState("");
  const [sensitiveAction, setSensitiveAction] = useState<SensitiveAction>(null);
  const decodedClientId = clientId ? decodeURIComponent(clientId) : "";
  const client = useQuery({
    queryKey: ["oauth-client", decodedClientId],
    enabled: !create && Boolean(decodedClientId),
    queryFn: ({ signal }) => apiRequest<OAuthClientSummary>(`/api/oauth/clients/${encodeURIComponent(decodedClientId)}`, { signal })
  });
  const current = client.data;
  const applications = useQuery({
    queryKey: ["applications", "oauth-client-editor"],
    enabled: canReadApplications,
    queryFn: ({ signal }) => fetchAllAsPage<ApplicationSummary>("/api/applications", signal)
  });
  const form = useForm<OAuthClientFormValues>({ resolver: zodResolver(oauthClientSchema), defaultValues: oauthClientDefaults() });
  const selectedGrants = useWatch({ control: form.control, name: "grantTypes" });

  useEffect(() => { if (client.data) form.reset(oauthClientDefaults(client.data)); }, [client.data, form]);

  const save = useMutation({
    mutationFn: async (values: OAuthClientFormValues) => {
      if (create) {
        const created = await apiRequest<OAuthClientCreated>("/api/oauth/clients", { method: "POST", body: JSON.stringify(oauthClientPayload(values, true)) });
        return { client: created.client, secret: created.clientSecret };
      }
      const updated = await apiRequest<OAuthClientSummary>(`/api/oauth/clients/${encodeURIComponent(decodedClientId)}`, { method: "PUT", body: JSON.stringify({ ...oauthClientPayload(values, false), version: client.data?.version }) });
      return { client: updated, secret: null };
    },
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ["oauth-clients"] });
      queryClient.setQueryData(["oauth-client", result.client.clientId], result.client);
      if (create) {
        if (result.secret) setSecret(result.secret);
        else navigate(`/oauth-clients/${encodeURIComponent(result.client.clientId)}`, { replace: true });
        return;
      }
      form.reset(oauthClientDefaults(result.client));
      setFeedback("La configuración del cliente OAuth quedó guardada.");
    }
  });
  const rotate = useMutation({
    mutationFn: (proofToken: string) => apiRequest<OAuthClientSecret>(`/api/oauth/clients/${encodeURIComponent(decodedClientId)}/rotate-secret`, { method: "POST", headers: { "X-AuthCenter-Reauthentication": proofToken } }),
    onSuccess: (result) => { setSensitiveAction(null); setSecret(result.clientSecret); }
  });
  const changeStatus = useMutation({
    mutationFn: async ({ proofToken, activate }: { proofToken: string; activate: boolean }) => {
      if (activate) {
        if (!current) throw new Error("El cliente OAuth todavía no termina de cargar.");
        await apiRequest<OAuthClientSummary>(`/api/oauth/clients/${encodeURIComponent(decodedClientId)}`, {
          method: "PUT",
          headers: { "X-AuthCenter-Reauthentication": proofToken },
          body: JSON.stringify({ ...oauthClientPayload({ ...oauthClientDefaults(current), isActive: true }, false), version: current.version })
        });
        return;
      }
      await apiRequest<void>(`/api/oauth/clients/${encodeURIComponent(decodedClientId)}`, { method: "DELETE", headers: { "X-AuthCenter-Reauthentication": proofToken } });
    },
    onSuccess: async () => {
      setSensitiveAction(null);
      setFeedback(current?.isActive ? "El cliente OAuth quedó desactivado y ya no puede emitir tokens nuevos." : "El cliente OAuth quedó activo de nuevo.");
      await Promise.all([queryClient.invalidateQueries({ queryKey: ["oauth-client", decodedClientId] }), queryClient.invalidateQueries({ queryKey: ["oauth-clients"] })]);
    }
  });

  if (!create && client.isPending) return <PageState title="Cargando cliente OAuth" busy />;
  if (!create && client.isError) return <PageState title="No pudimos cargar el cliente OAuth" detail={errorMessage(client.error)} tone="error" action={<Link className="button" to="/oauth-clients">Volver</Link>} />;
  if (create && !canReadApplications) return <PageState title="No puedes registrar clientes OAuth" detail={needPermission("AUTHCENTER_APPLICATIONS_READ", "elegir la aplicación propietaria")} tone="error" action={<Link className="button" to="/oauth-clients">Volver</Link>} />;

  const title = create ? "Nuevo cliente OAuth" : current?.displayName ?? "Cliente OAuth";
  const hasAuthorizationCode = selectedGrants.includes("authorization_code");
  const secretOwner = current?.clientId ?? form.getValues("clientId");
  const actionError = rotate.error ?? changeStatus.error;

  function closeSecret(): void {
    setSecret("");
    if (create) navigate(`/oauth-clients/${encodeURIComponent(secretOwner)}`, { replace: true });
  }

  return <>
    <Breadcrumbs items={[{ label: "Clientes OAuth", to: "/oauth-clients" }, { label: title }]} />
    <PageHeader eyebrow={create ? "Alta" : current?.applicationCode ?? "Integraciones"} title={title} description={create ? "Conecta una aplicación para que sus usuarios inicien sesión con AuthCenter: a dónde vuelven y qué datos puede pedir." : canWrite ? "Configura cómo inicia sesión esta aplicación. El secreto actual no se puede volver a ver: si lo perdiste, rótalo." : "Consulta la configuración efectiva. Tu acceso actual es de sólo lectura."} actions={<>{create ? null : <HistoryLink entityName="OAuthClient" entityId={clientId} />}<Link className="button button--secondary" to="/oauth-clients">Volver al listado</Link></>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    <SaveError error={save.error} onReload={() => { save.reset(); void client.refetch().then((fresh) => { if (fresh.data) form.reset(oauthClientDefaults(fresh.data)); }); }} />
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => save.mutateAsync(values))(event)}>
      <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="oauth-identity">
          <div className="settings-panel__heading"><div><h2 id="oauth-identity">Identidad</h2><p>La aplicación, el client ID y el tipo quedan fijos después del alta.</p></div>{current ? <StatusBadge active={current.isActive} /> : null}</div>
          <div className="form-grid">
            <Field label="Aplicación" error={form.formState.errors.applicationSystemId?.message}><select {...form.register("applicationSystemId")} disabled={!create}><option value="">Selecciona una aplicación</option>{applications.data?.items.filter((application) => application.isActive).map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></Field>
            <Field label="Nombre" error={form.formState.errors.displayName?.message}><input {...form.register("displayName")} autoComplete="off" /></Field>
            <Field label="Client ID" error={form.formState.errors.clientId?.message}><input {...form.register("clientId")} disabled={!create} autoComplete="off" /></Field>
            <Field label="Tipo" error={form.formState.errors.clientType?.message}><select {...form.register("clientType")} disabled={!create}><option value="0">Confidencial</option><option value="1">Público</option></select></Field>
          </div>
        </section>
        <section className="settings-panel" aria-labelledby="oauth-flow">
          <div className="settings-panel__heading"><div><h2 id="oauth-flow">Flujos y datos</h2><p>Concede sólo lo necesario. El flujo de código de autorización siempre exige PKCE.</p></div></div>
          <fieldset className="check-group"><legend>Flujos permitidos</legend><div className="checkbox-grid">{oauthGrants.map((grant) => <Checkbox key={grant} label={<>{grantLabels[grant]} <code>{grant === tokenExchangeGrant ? "RFC 8693" : grant}</code></>} registration={form.register("grantTypes")} value={grant} />)}</div>{form.formState.errors.grantTypes ? <p className="field-error">{form.formState.errors.grantTypes.message}</p> : null}</fieldset>
          <fieldset className="check-group"><legend>Datos que puede pedir (scopes)</legend><div className="checkbox-grid">{oauthScopes.map((scope) => <Checkbox key={scope} label={<>{scopeLabels[scope]} <code>{scope}</code></>} registration={form.register("allowedScopes")} value={scope} />)}</div>{form.formState.errors.allowedScopes ? <p className="field-error">{form.formState.errors.allowedScopes.message}</p> : null}</fieldset>
          <Controller control={form.control} name="apiScopes" render={({ field, fieldState }) => <ApiScopePicker value={field.value} onChange={field.onChange} disabled={!canWrite} error={fieldState.error?.message} />} />
          <Field label="URLs de regreso (redirect_uri)" error={form.formState.errors.redirectUris?.message} help="Una por línea y exactas: sin comodines, fragmentos ni credenciales."><textarea {...form.register("redirectUris")} rows={4} disabled={!hasAuthorizationCode && !form.getValues("redirectUris")} placeholder="https://app.example.com/oauth/callback" /></Field>
          <div className="form-grid">
            <Field label="URL de inicio de sesión" error={form.formState.errors.loginUrl?.message}><input {...form.register("loginUrl")} type="url" placeholder="https://app.example.com/login" /></Field>
            <Field label="Vigencia del token de acceso (segundos)" error={form.formState.errors.accessTokenLifetimeSeconds?.message}><input {...form.register("accessTokenLifetimeSeconds", { valueAsNumber: true })} type="number" min={60} max={3600} /></Field>
          </div>
          <div className="checkbox-grid"><Checkbox label="Requerir PKCE" registration={form.register("requirePkce")} /><Checkbox label="Omitir la pantalla de consentimiento" registration={form.register("autoConsent")} /></div>
          <Field label="Orígenes CORS del navegador" error={form.formState.errors.allowedCorsOrigins?.message} help="Sólo para aplicaciones de una página que canjean el código con PKCE desde el navegador. Uno por línea: https://app.example.com. Nunca incluye credenciales."><textarea {...form.register("allowedCorsOrigins")} rows={2} placeholder="https://app.example.com" /></Field>
        </section>
        <section className="settings-panel" aria-labelledby="oauth-logout">
          <div className="settings-panel__heading"><div><h2 id="oauth-logout">Cierre de sesión</h2><p>Registra a dónde puede volver el usuario tras cerrar sesión y dónde AuthCenter avisa a la aplicación cuando la sesión termina.</p></div></div>
          <Field label="URLs después de cerrar sesión (post_logout_redirect_uri)" error={form.formState.errors.postLogoutRedirectUris?.message} help="Una por línea. Con AuthCenter.Client registra https://tu-app/signout-callback-authcenter."><textarea {...form.register("postLogoutRedirectUris")} rows={3} disabled={!hasAuthorizationCode && !form.getValues("postLogoutRedirectUris")} placeholder="https://app.example.com/signout-callback-authcenter" /></Field>
          <Field label="URL de aviso de cierre de sesión (back-channel)" error={form.formState.errors.backchannelLogoutUri?.message} help="AuthCenter avisa aquí con un token firmado (logout token) cuando termina la sesión. Con AuthCenter.Client: https://tu-app/auth/backchannel-logout."><input {...form.register("backchannelLogoutUri")} type="url" disabled={!hasAuthorizationCode && !form.getValues("backchannelLogoutUri")} placeholder="https://app.example.com/auth/backchannel-logout" /></Field>
          <Checkbox label="Incluir el identificador de sesión (sid) en el aviso" registration={form.register("backchannelLogoutSessionRequired")} />
        </section>
      </fieldset>
      {canWrite ? <div className="form-footer"><Link className="button button--secondary" to="/oauth-clients">Cancelar</Link><button className="button" type="submit" disabled={save.isPending}>{save.isPending ? "Guardando…" : create ? "Crear cliente OAuth" : "Guardar configuración"}</button></div> : <p className="muted">{askForPermission("AUTHCENTER_OAUTH_CLIENTS_WRITE", "cambiar esta configuración")}</p>}
    </form>
    {current && canWrite ? <section className="settings-panel settings-panel--actions" aria-labelledby="oauth-actions"><div className="settings-panel__heading"><div><h2 id="oauth-actions">Credencial y estado</h2><p>Estas acciones requieren comprobar de nuevo tu identidad y consumen una prueba de un solo uso.</p></div></div><div className="button-group">{current.clientType === 0 ? <button className="button button--secondary" type="button" onClick={() => { rotate.reset(); setSensitiveAction("rotate"); }}>Rotar secreto</button> : null}<button className={current.isActive ? "button button--danger-quiet" : "button button--secondary"} type="button" onClick={() => { changeStatus.reset(); setSensitiveAction(current.isActive ? "deactivate" : "activate"); }}>{current.isActive ? "Desactivar cliente" : "Activar cliente"}</button></div>{actionError ? <p className="alert alert--error" role="alert">{errorMessage(actionError)}</p> : null}</section> : null}
    <ReauthenticationDialog open={sensitiveAction !== null} purpose={sensitiveAction === "rotate" ? "admin.oauth-client.rotate-secret" : sensitiveAction === "activate" ? "admin.oauth-client.activate" : "admin.oauth-client.deactivate"} title={sensitiveAction === "rotate" ? "Rotar el secreto del cliente" : sensitiveAction === "activate" ? "Activar cliente OAuth" : "Desactivar cliente OAuth"} detail={sensitiveAction === "rotate" ? "El secreto actual dejará de funcionar de inmediato. Asegúrate de poder actualizar la aplicación que lo usa." : sensitiveAction === "activate" ? "El cliente volverá a poder emitir tokens con sus flujos y permisos actuales." : "Se bloqueará la emisión de tokens nuevos para este cliente."} confirmLabel={sensitiveAction === "rotate" ? "Verificar y rotar" : sensitiveAction === "activate" ? "Verificar y activar" : "Verificar y desactivar"} dangerous={sensitiveAction === "deactivate"} onCancel={() => setSensitiveAction(null)} onProof={async (proof) => { if (sensitiveAction === "rotate") await rotate.mutateAsync(proof); else if (sensitiveAction) await changeStatus.mutateAsync({ proofToken: proof, activate: sensitiveAction === "activate" }); }} />
    <SecretRevealDialog open={Boolean(secret)} secret={secret} title={`Secreto para ${secretOwner}`} onClose={closeSecret} />
  </>;
}

function Field({ label, error, help, children }: { label: string; error: string | undefined; help?: string; children: ReactNode }) { return <label className="field"><span>{label}</span>{children}{help ? <span className="field-help">{help}</span> : null}{error ? <span className="field-error">{error}</span> : null}</label>; }
function Checkbox({ label, registration, value }: { label: ReactNode; registration: UseFormRegisterReturn; value?: string }) { return <label className="checkbox-field"><input type="checkbox" {...registration} value={value} /><span>{label}</span></label>; }
