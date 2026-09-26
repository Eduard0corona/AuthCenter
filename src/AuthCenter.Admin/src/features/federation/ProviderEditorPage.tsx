import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type ReactNode } from "react";
import { useFieldArray, useForm, useWatch } from "react-hook-form";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { apiRequest, ApiError } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, DirectoryGroupSummary, FederationConnectionTest, FederationProvider, FederationServiceProvider, PagedResult } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { HistoryLink } from "../../components/HistoryLink";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { ReauthenticationDialog } from "../../components/ReauthenticationDialog";
import { StatusBadge } from "../../components/StatusBadge";
import { connectionCheckLabels, federationProviderDefaults, federationProviderFromResponse, federationProviderPayload, federationProviderSchema, type FederationProviderFormValues } from "./federation";

type SensitiveAction = "save" | "delete" | null;

export default function ProviderEditorPage({ create = false }: { create?: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_FEDERATION_WRITE");
  const canReadGroups = permissions.has("AUTHCENTER_GROUPS_READ");
  const { providerId = "" } = useParams();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState("");
  const [sensitiveAction, setSensitiveAction] = useState<SensitiveAction>(null);
  const providers = useQuery({
    queryKey: ["federation-providers", "all"],
    enabled: !create && Boolean(providerId),
    queryFn: ({ signal }) => apiRequest<FederationProvider[]>("/api/federation/providers", { signal })
  });
  const applications = useQuery({
    queryKey: ["applications", "federation-provider-editor"],
    queryFn: ({ signal }) => apiRequest<PagedResult<ApplicationSummary>>("/api/applications?page=1&pageSize=100", { signal })
  });
  // The values to register at the upstream IdP (hosted OIDC callback, SAML entity ID and ACS).
  const serviceProvider = useQuery({
    queryKey: ["federation-service-provider"],
    queryFn: ({ signal }) => apiRequest<FederationServiceProvider>("/api/federation/service-provider", { signal })
  });
  const groups = useQuery({
    queryKey: ["groups", "federation-provider-editor"],
    enabled: canReadGroups,
    queryFn: ({ signal }) => apiRequest<PagedResult<DirectoryGroupSummary>>("/api/groups?page=1&pageSize=100&isActive=true", { signal })
  });
  const current = providers.data?.find((provider) => provider.id === providerId);
  const form = useForm<FederationProviderFormValues>({ resolver: zodResolver(federationProviderSchema), defaultValues: { ...federationProviderDefaults(), applicationSystemId: params.get("applicationId") ?? "" } });
  const { reset } = form;
  useEffect(() => { if (current) reset(federationProviderFromResponse(current)); }, [current, reset]);
  const protocol = useWatch({ control: form.control, name: "protocol" });
  const mappings = useFieldArray({ control: form.control, name: "groupMappings" });
  const connectionTest = useMutation({
    mutationFn: () => apiRequest<FederationConnectionTest>(`/api/federation/providers/${providerId}/test`, { method: "POST" })
  });

  const save = useMutation({
    mutationFn: ({ proofToken, values }: { proofToken: string; values: FederationProviderFormValues }) => apiRequest<FederationProvider>(create ? "/api/federation/providers" : `/api/federation/providers/${providerId}`, { method: create ? "POST" : "PUT", body: JSON.stringify(federationProviderPayload(values, current?.version ?? 0)), headers: { "X-AuthCenter-Reauthentication": proofToken } }),
    onSuccess: async (result) => {
      setSensitiveAction(null);
      await queryClient.invalidateQueries({ queryKey: ["federation-providers"] });
      await queryClient.invalidateQueries({ queryKey: ["federation-routing-rules"] });
      if (create) { navigate(`/federation/providers/${result.id}`, { replace: true }); return; }
      form.setValue("clientSecret", "");
      form.setValue("samlSigningCertificatePem", "");
      setFeedback(`El proveedor quedó guardado como versión ${result.version}.`);
    }
  });
  const remove = useMutation({
    mutationFn: (proofToken: string) => apiRequest<void>(`/api/federation/providers/${providerId}`, { method: "DELETE", headers: { "X-AuthCenter-Reauthentication": proofToken } }),
    onSuccess: async () => {
      setSensitiveAction(null);
      await Promise.all([queryClient.invalidateQueries({ queryKey: ["federation-providers"] }), queryClient.invalidateQueries({ queryKey: ["federation-routing-rules"] })]);
      navigate(`/federation?applicationId=${current?.applicationSystemId ?? ""}`, { replace: true });
    }
  });

  if (!create && providers.isPending) return <PageState title="Cargando proveedor de federación" busy />;
  if (!create && providers.isError) return <PageState title="No pudimos cargar el proveedor" detail={errorMessage(providers.error)} tone="error" action={<Link className="button" to="/federation">Volver</Link>} />;
  if (!create && providers.data && !current) return <PageState title="Proveedor no encontrado" detail="El proveedor no existe o fue eliminado." tone="error" action={<Link className="button" to="/federation">Volver</Link>} />;
  const title = create ? "Nuevo proveedor de federación" : current?.name ?? "Proveedor";
  const conflict = save.error instanceof ApiError && save.error.code === "CONCURRENCY_CONFLICT";
  const actionError = conflict ? null : (save.error ?? remove.error);
  const backTo = `/federation${current ? `?applicationId=${current.applicationSystemId}` : params.get("applicationId") ? `?applicationId=${params.get("applicationId")}` : ""}`;
  const metadataUrl = current?.protocol === "Saml2" ? `/api/federation/saml/${current.id}/metadata` : null;

  return <>
    <Breadcrumbs items={[{ label: "Federación", to: backTo }, { label: title }]} />
    <PageHeader eyebrow={create ? "Alta" : current?.protocol === "Saml2" ? "SAML 2.0" : "OpenID Connect"} title={title} description={create ? "Registra un IdP corporativo. Los secretos y certificados se almacenan protegidos y nunca se devuelven." : canWrite ? "Cada cambio requiere reautenticación. El client secret y el certificado solo se reemplazan si escribes uno nuevo." : "Consulta la configuración del proveedor. No tienes permisos de escritura."} actions={<>{create ? null : <HistoryLink entityName="FederationProvider" entityId={providerId} />}<Link className="button button--secondary" to={backTo}>Volver a federación</Link></>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    {conflict ? <p className="alert alert--error" role="alert">El proveedor cambió desde que lo cargaste. Recarga para ver la versión vigente antes de volver a guardar. <button className="button button--small button--secondary" type="button" onClick={() => { save.reset(); void providers.refetch(); }}>Recargar</button></p> : null}
    {actionError ? <p className="alert alert--error" role="alert">{errorMessage(actionError)}</p> : null}
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit(() => { save.reset(); setSensitiveAction("save"); })(event)}>
      <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="provider-identity">
          <div className="settings-panel__heading"><div><h2 id="provider-identity">Identidad</h2><p>El issuer se valida contra el ID token (OIDC, URL HTTPS) o contra la aserción SAML (entity ID: https, http o urn).</p></div>{current ? <StatusBadge active={current.isActive} /> : null}</div>
          <div className="form-grid">
            {create ? <Field label="Aplicación" error={form.formState.errors.applicationSystemId?.message}><select key={applications.data ? "loaded" : "loading"} {...form.register("applicationSystemId")}><option value="">Selecciona una aplicación</option>{applications.data?.items.filter((application) => application.isActive).map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></Field>
              : <Field label="Aplicación" error={undefined}><input value={applications.data?.items.find((application) => application.id === current?.applicationSystemId)?.name ?? current?.applicationSystemId ?? ""} readOnly /></Field>}
            <Field label="Nombre" error={form.formState.errors.name?.message}><input {...form.register("name")} autoComplete="off" placeholder="Entra ID corporativo" /></Field>
            {create ? <Field label="Protocolo" error={form.formState.errors.protocol?.message}><select {...form.register("protocol")}><option value="Oidc">OpenID Connect</option><option value="Saml2">SAML 2.0</option></select></Field>
              : <Field label="Protocolo" error={undefined} help="El protocolo no se cambia; crea otro proveedor si es necesario."><input value={current?.protocol === "Saml2" ? "SAML 2.0" : "OpenID Connect"} readOnly /></Field>}
            <Field label="Issuer" error={form.formState.errors.issuer?.message}><input {...form.register("issuer")} className="mono" autoComplete="off" spellCheck={false} placeholder="https://login.example.test" /></Field>
          </div>
        </section>
        {/* Distinct keys: switching protocol (or loading a SAML provider) must remount the panel instead of turning uncontrolled inputs into controlled ones. */}
        {protocol === "Oidc" ? <section key="oidc" className="settings-panel" aria-labelledby="provider-oidc">
          <div className="settings-panel__heading"><div><h2 id="provider-oidc">OpenID Connect</h2><p>Se usa authorization code con PKCE y nonce. Registra en el IdP el callback hospedado de AuthCenter; es el único que funciona desde el login hospedado.</p></div>{current?.hasClientSecret ? <span className="tag">Secret configurado</span> : current ? <span className="tag tag--warning">Sin secret</span> : null}</div>
          <div className="form-grid">
            <Field label="Client ID" error={form.formState.errors.clientId?.message}><input {...form.register("clientId")} className="mono" autoComplete="off" spellCheck={false} /></Field>
            <Field label="Callback URL" error={form.formState.errors.oidcCallbackUrl?.message} help={serviceProvider.data?.oidcCallbackUrl ? `Déjalo vacío para usar el callback hospedado: ${serviceProvider.data.oidcCallbackUrl}` : "Déjalo vacío para usar el callback hospedado de AuthCenter."}><input {...form.register("oidcCallbackUrl")} className="mono" autoComplete="off" spellCheck={false} placeholder={serviceProvider.data?.oidcCallbackUrl ?? "https://authcenter.example.test/api/federation/oidc/callback"} /></Field>
            <Field label="Discovery endpoint" error={form.formState.errors.discoveryEndpoint?.message} help="Opcional; por defecto se usa issuer/.well-known/openid-configuration."><input {...form.register("discoveryEndpoint")} className="mono" autoComplete="off" spellCheck={false} /></Field>
            <Field label={create ? "Client secret" : "Nuevo client secret"} error={form.formState.errors.clientSecret?.message} help={create ? "Opcional para clientes públicos. Se protege en reposo y nunca se muestra." : "Déjalo vacío para conservar el secret actual."}><input type="password" {...form.register("clientSecret")} autoComplete="new-password" /></Field>
          </div>
          <div className="checkbox-grid">
            <label className="checkbox-field"><input type="checkbox" {...form.register("requireVerifiedEmail")} /><span>Exigir email_verified del IdP. Sin él, solo se confía en los correos de los dominios de sus routing rules.</span></label>
          </div>
        </section> : <section key="saml" className="settings-panel" aria-labelledby="provider-saml">
          <div className="settings-panel__heading"><div><h2 id="provider-saml">SAML 2.0</h2><p>La respuesta o la aserción deben venir firmadas (SHA-256 o superior) con el certificado registrado; se aceptan aserciones cifradas para AuthCenter. Solo se muestra la huella del certificado.</p></div>{current?.samlSigningCertificateThumbprint ? <span className="tag mono">SHA-1 {current.samlSigningCertificateThumbprint}</span> : null}</div>
          <div className="form-grid">
            <Field label="URL de Single Sign-On" error={form.formState.errors.samlSingleSignOnUrl?.message}><input {...form.register("samlSingleSignOnUrl")} className="mono" autoComplete="off" spellCheck={false} placeholder="https://idp.example.test/sso" /></Field>
            {metadataUrl ? <Field label="Metadata del SP" error={undefined} help="Entrega este documento al IdP para registrar AuthCenter."><input value={`${window.location.origin}${metadataUrl}`} readOnly className="mono" /></Field> : null}
            {serviceProvider.data?.samlEntityId ? <Field label="Entity ID de AuthCenter" error={undefined}><input value={serviceProvider.data.samlEntityId} readOnly className="mono" /></Field> : null}
            {serviceProvider.data?.samlAssertionConsumerServiceUrl ? <Field label="ACS de AuthCenter" error={undefined}><input value={serviceProvider.data.samlAssertionConsumerServiceUrl} readOnly className="mono" /></Field> : null}
          </div>
          <Field label={current?.samlSigningCertificateThumbprint ? "Nuevo certificado de firma (PEM)" : "Certificado de firma (PEM)"} error={form.formState.errors.samlSigningCertificatePem?.message} help={current?.samlSigningCertificateThumbprint ? "Déjalo vacío para conservar el certificado actual." : "Pega el certificado X.509 público en formato PEM."}><textarea {...form.register("samlSigningCertificatePem")} className="mono" rows={6} spellCheck={false} placeholder="-----BEGIN CERTIFICATE-----" /></Field>
        </section>}
        <section className="settings-panel" aria-labelledby="provider-lifecycle">
          <div className="settings-panel__heading"><div><h2 id="provider-lifecycle">Cuentas y vinculación</h2><p>Controla si el IdP puede crear usuarios y cómo se vinculan con cuentas existentes.</p></div></div>
          <div className="form-grid">
            <Field label="Vinculación de cuentas" error={form.formState.errors.accountLinkingMode?.message} help="Con Disabled, una identidad externa nunca se asocia a una cuenta ya existente."><select {...form.register("accountLinkingMode")}><option value="Disabled">Disabled</option><option value="VerifiedEmail">Por correo verificado</option></select></Field>
          </div>
          <div className="checkbox-grid">
            <label className="checkbox-field"><input type="checkbox" {...form.register("jitProvisioningEnabled")} /><span>Just-in-time provisioning: crear usuarios en el primer acceso</span></label>
            <label className="checkbox-field"><input type="checkbox" {...form.register("isActive")} /><span>Proveedor activo</span></label>
          </div>
        </section>
        <section className="settings-panel" aria-labelledby="provider-groups">
          <div className="settings-panel__heading"><div><h2 id="provider-groups">MFA y grupos del IdP</h2><p>El IdP es la fuente de los grupos mapeados: en cada inicio de sesión se agregan y quitan esas membresías. Los demás grupos no cambian.</p></div></div>
          <div className="checkbox-grid">
            <label className="checkbox-field"><input type="checkbox" {...form.register("trustUpstreamMfa")} /><span>Confiar en el MFA del IdP (amr "mfa" en OIDC, contexto de autenticación multifactor en SAML)</span></label>
          </div>
          <div className="form-grid">
            <Field label="Claim o atributo de grupos" error={form.formState.errors.groupsClaim?.message} help="Por ejemplo groups. Déjalo vacío para no sincronizar grupos."><input {...form.register("groupsClaim")} className="mono" autoComplete="off" spellCheck={false} placeholder="groups" /></Field>
          </div>
          {form.formState.errors.groupMappings?.message ? <p className="field-error" role="alert">{form.formState.errors.groupMappings.message}</p> : null}
          {mappings.fields.length ? <div className="data-table" tabIndex={0} role="region" aria-label="Mapeo de grupos, desplazamiento horizontal"><table><caption className="sr-only">Mapeo de valores del IdP a grupos del directorio</caption><thead><tr><th>Valor del IdP</th><th>Grupo del directorio</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{mappings.fields.map((field, index) => {
            const groupId = form.getValues(`groupMappings.${index}.directoryGroupId`);
            const known = groups.data?.items.some((group) => group.id === groupId);
            const storedName = current?.groupMappings?.find((mapping) => mapping.directoryGroupId === groupId)?.directoryGroupName;
            return <tr key={field.id}>
              <td><label className="field"><span className="sr-only">Valor del IdP {index + 1}</span><input {...form.register(`groupMappings.${index}.upstreamValue`)} className="mono" autoComplete="off" spellCheck={false} aria-label={`Valor del IdP ${index + 1}`} />{form.formState.errors.groupMappings?.[index]?.upstreamValue?.message ? <span className="field-error">{form.formState.errors.groupMappings[index]?.upstreamValue?.message}</span> : null}</label></td>
              <td><label className="field"><span className="sr-only">Grupo {index + 1}</span><select {...form.register(`groupMappings.${index}.directoryGroupId`)} aria-label={`Grupo ${index + 1}`}><option value="">Selecciona un grupo</option>{groupId && !known ? <option value={groupId}>{storedName ?? `Grupo actual (${groupId})`}</option> : null}{groups.data?.items.map((group) => <option key={group.id} value={group.id}>{group.name}</option>)}</select>{form.formState.errors.groupMappings?.[index]?.directoryGroupId?.message ? <span className="field-error">{form.formState.errors.groupMappings[index]?.directoryGroupId?.message}</span> : null}</label></td>
              <td className="table-action"><button className="button button--small button--danger-quiet" type="button" onClick={() => mappings.remove(index)}>Quitar</button></td>
            </tr>;
          })}</tbody></table></div> : null}
          <div className="button-group"><button className="button button--secondary" type="button" onClick={() => mappings.append({ upstreamValue: "", directoryGroupId: "" })}>Agregar mapeo de grupo</button></div>
        </section>
      </fieldset>
      <div className="form-footer">
        <Link className="button button--secondary" to={backTo}>Cancelar</Link>
        {canWrite ? <button className="button" type="submit" disabled={save.isPending}>{create ? "Verificar y crear" : "Verificar y guardar"}</button> : null}
      </div>
    </form>
    {current && canWrite ? <section className="settings-panel settings-panel--actions" aria-labelledby="provider-diagnostics">
      <div className="settings-panel__heading"><div><h2 id="provider-diagnostics">Probar conexión</h2><p>Consulta el discovery, las llaves y los certificados del IdP con la configuración guardada. No cambia nada.</p></div>{connectionTest.data ? <span className={connectionTest.data.succeeded ? "tag" : "tag tag--warning"}>{connectionTest.data.succeeded ? "Sin errores" : "Con errores"}</span> : null}</div>
      {connectionTest.error ? <p className="alert alert--error" role="alert">{errorMessage(connectionTest.error)}</p> : null}
      {connectionTest.data ? <ul className="check-list" aria-label="Resultado de la prueba de conexión">{connectionTest.data.checks.map((check) => <li key={check.name}><span className={check.status === "Pass" ? "tag" : "tag tag--warning"}>{check.status === "Pass" ? "Correcto" : check.status === "Warning" ? "Advertencia" : "Error"}</span> <strong>{connectionCheckLabels[check.name] ?? check.name}</strong>: {check.detail}</li>)}</ul> : null}
      <div className="button-group"><button className="button button--secondary" type="button" disabled={connectionTest.isPending} onClick={() => connectionTest.mutate()}>{connectionTest.isPending ? "Probando…" : "Probar conexión"}</button></div>
    </section> : null}
    {current && canWrite ? <section className="settings-panel settings-panel--actions" aria-labelledby="provider-danger">
      <div className="settings-panel__heading"><div><h2 id="provider-danger">Eliminar proveedor</h2><p>Sus routing rules dejarán de aplicarse y los usuarios federados no podrán iniciar sesión con este IdP.</p></div></div>
      <div className="button-group"><button className="button button--danger-quiet" type="button" onClick={() => { remove.reset(); setSensitiveAction("delete"); }}>Eliminar proveedor</button></div>
    </section> : null}
    <ReauthenticationDialog open={sensitiveAction !== null} purpose="admin.federation.change" title={sensitiveAction === "delete" ? "Eliminar proveedor de federación" : create ? "Crear proveedor de federación" : "Guardar proveedor de federación"} detail={sensitiveAction === "delete" ? "Esta acción no se puede deshacer y queda auditada." : "Los cambios de federación afectan cómo inician sesión los usuarios de la aplicación."} confirmLabel={sensitiveAction === "delete" ? "Verificar y eliminar" : create ? "Verificar y crear" : "Verificar y guardar"} dangerous={sensitiveAction === "delete"} onCancel={() => setSensitiveAction(null)} onProof={async (proof) => { if (sensitiveAction === "delete") await remove.mutateAsync(proof); else await save.mutateAsync({ proofToken: proof, values: form.getValues() }); }} />
  </>;
}

function Field({ label, error, help, children }: { label: string; error: string | undefined; help?: string; children: ReactNode }) { return <label className="field"><span>{label}</span>{children}{help ? <span className="field-help">{help}</span> : null}{error ? <span className="field-error">{error}</span> : null}</label>; }
