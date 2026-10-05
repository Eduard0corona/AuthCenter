import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useId, useState } from "react";
import { useFieldArray, useForm } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { useApplicationsCatalog } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ProfileAttributeDefinition, SamlCertificate, SamlServiceProvider, SamlServiceProviderMetadata } from "../../api/types";
import { needPermission } from "../../auth/permissions";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { Field } from "../../components/Field";
import { HistoryLink } from "../../components/HistoryLink";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { SaveError } from "../../components/SaveError";
import { StatusBadge } from "../../components/StatusBadge";
import { formatDate } from "../../utils/format";
import { applyMetadata, attributeSources, createSamlAppPayload, nameIdFormatLabels, nameIdFormats, newSamlAppSchema, samlAppDefaults, samlAppSchema, updateSamlAppPayload, type SamlAppFormValues } from "./saml-app";
import { IdentityProviderPanel } from "./SamlAppsPage";

const SAML_ERRORS: Record<string, string> = {
  APP_NOT_FOUND: "La aplicación no existe o está inactiva.",
  SAML_SP_EXISTS: "Otra aplicación SAML ya usa ese entity ID.",
  SAML_SP_NOT_FOUND: "La aplicación SAML no existe.",
  SAML_METADATA_INVALID: "No pudimos leer los metadatos: pega el XML completo del proveedor (EntityDescriptor con SPSSODescriptor)."
};

export default function SamlAppEditorRoute({ create = false }: { create?: boolean }) {
  const { providerId = "" } = useParams();
  return <SamlAppEditorPage key={create ? "new" : providerId} create={create} />;
}

function SamlAppEditorPage({ create }: { create: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_SAML_APPS_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const canReadSchema = permissions.has("AUTHCENTER_PROFILE_SCHEMAS_READ");
  const { providerId = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const metadataId = useId();
  const [feedback, setFeedback] = useState("");
  const [metadataXml, setMetadataXml] = useState("");
  const [metadataWarnings, setMetadataWarnings] = useState<string[]>([]);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const provider = useQuery({
    queryKey: ["saml-app", providerId],
    enabled: !create && Boolean(providerId),
    queryFn: ({ signal }) => apiRequest<SamlServiceProvider>(`/api/saml/service-providers/${providerId}`, { signal })
  });
  const applications = useApplicationsCatalog(create && canReadApplications);
  const schema = useQuery({
    queryKey: ["profile-schema", "active"],
    enabled: canReadSchema,
    queryFn: ({ signal }) => apiRequest<ProfileAttributeDefinition[]>("/api/profile-schema", { signal })
  });
  const form = useForm<SamlAppFormValues>({ resolver: zodResolver(create ? newSamlAppSchema : samlAppSchema), defaultValues: samlAppDefaults() });
  const attributes = useFieldArray({ control: form.control, name: "attributes" });
  const current = provider.data;
  const { reset } = form;
  useEffect(() => { if (current) reset(samlAppDefaults(current)); }, [current, reset]);

  const save = useMutation({
    mutationFn: (values: SamlAppFormValues) => create
      ? apiRequest<SamlServiceProvider>("/api/saml/service-providers", { method: "POST", body: JSON.stringify(createSamlAppPayload(values)) })
      : apiRequest<SamlServiceProvider>(`/api/saml/service-providers/${providerId}`, { method: "PUT", body: JSON.stringify(updateSamlAppPayload(values, current?.version ?? 0)) }),
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: ["saml-apps"] });
      if (create) { navigate(`/saml-apps/${saved.id}`, { replace: true }); return; }
      queryClient.setQueryData(["saml-app", providerId], saved);
      setFeedback(`La aplicación SAML quedó guardada como versión ${saved.version}.`);
    }
  });
  const parse = useMutation({
    mutationFn: () => apiRequest<SamlServiceProviderMetadata>("/api/saml/service-providers/parse-metadata", { method: "POST", body: JSON.stringify({ metadataXml }) }),
    onSuccess: (metadata) => {
      reset(applyMetadata(form.getValues(), metadata), { keepDefaultValues: true });
      setMetadataWarnings(metadata.warnings);
      setFeedback("Los metadatos llenaron el formulario. Revisa los datos y guarda.");
    }
  });
  const remove = useMutation({
    mutationFn: () => apiRequest<void>(`/api/saml/service-providers/${providerId}`, { method: "DELETE" }),
    onSuccess: async () => {
      setConfirmDelete(false);
      await queryClient.invalidateQueries({ queryKey: ["saml-apps"] });
      navigate("/saml-apps", { replace: true });
    }
  });

  if (!create && provider.isPending) return <PageState title="Cargando aplicación SAML" busy />;
  if (!create && provider.isError) return <PageState title="No pudimos cargar la aplicación SAML" detail={errorMessage(provider.error, SAML_ERRORS)} tone="error" action={<Link className="button" to="/saml-apps">Volver</Link>} />;
  if (create && !canReadApplications) return <PageState title="No puedes registrar aplicaciones SAML" detail={needPermission("AUTHCENTER_APPLICATIONS_READ", "elegir la aplicación de AuthCenter a la que pertenece")} tone="error" action={<Link className="button" to="/saml-apps">Volver</Link>} />;
  const title = create ? "Nueva aplicación SAML" : current?.name ?? "Aplicación SAML";
  const errors = form.formState.errors;
  const sources = [...attributeSources, ...(schema.data?.filter((definition) => definition.isActive).map((definition) => ({ value: `profile:${definition.key}`, label: `Perfil: ${definition.displayName} (${definition.key})` })) ?? [])];

  return <>
    <Breadcrumbs items={[{ label: "Aplicaciones SAML", to: "/saml-apps" }, { label: title }]} />
    <PageHeader
      eyebrow={create ? "Alta" : current?.applicationName ?? "Aplicaciones"}
      title={title}
      description={create ? "Importa los metadatos de la aplicación o llena sus datos. Sus usuarios serán los de la aplicación de AuthCenter que elijas." : canWrite ? "Ajusta los endpoints, la aserción y los atributos. Los cambios aplican al siguiente inicio de sesión." : "Consulta la configuración. Tu acceso es de sólo lectura."}
      actions={<>{create ? null : <HistoryLink entityName="SamlServiceProvider" entityId={providerId} />}<Link className="button button--secondary" to="/saml-apps">Volver al listado</Link></>}
    />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    <SaveError error={save.error} messages={SAML_ERRORS} onReload={() => { save.reset(); void provider.refetch().then((fresh) => { if (fresh.data) reset(samlAppDefaults(fresh.data)); }); }} />
    {canWrite ? <section className="settings-panel" aria-labelledby="saml-metadata">
      <div className="settings-panel__heading"><div><h2 id="saml-metadata">Importar metadatos de la aplicación</h2><p>Pega el XML de metadatos del proveedor de servicio: llena el entity ID, las URLs, los certificados y el formato de NameID. No se guarda nada hasta que guardes el formulario.</p></div></div>
      <label className="field" htmlFor={metadataId}><span>Metadatos XML</span><textarea id={metadataId} className="mono" rows={5} value={metadataXml} onChange={(event) => setMetadataXml(event.target.value)} spellCheck={false} /></label>
      {parse.error ? <p className="alert alert--error" role="alert">{errorMessage(parse.error, SAML_ERRORS)}</p> : null}
      {metadataWarnings.length ? <ul className="alert alert--info">{metadataWarnings.map((warning) => <li key={warning}>{warning}</li>)}</ul> : null}
      <div className="button-group"><button className="button button--secondary" type="button" disabled={!metadataXml.trim() || parse.isPending} onClick={() => { setFeedback(""); parse.mutate(); }}>{parse.isPending ? "Leyendo…" : "Leer metadatos"}</button></div>
    </section> : null}
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => { setFeedback(""); return save.mutateAsync(values).catch(() => undefined); })(event)}>
      <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="saml-identity">
          <div className="settings-panel__heading"><div><h2 id="saml-identity">Identidad</h2><p>El entity ID es el emisor de las solicitudes de la aplicación y la audiencia de sus aserciones.</p></div>{current ? <StatusBadge active={current.isActive} activeLabel="Activa" inactiveLabel="Inactiva" /> : null}</div>
          <div className="form-grid">
            {create
              ? <Field label="Aplicación de AuthCenter" error={errors.applicationSystemId?.message} help="Sus usuarios, política de acceso y MFA se aplican a esta aplicación SAML."><select {...form.register("applicationSystemId")}><option value="">Selecciona una aplicación</option>{applications.data?.filter((application) => application.isActive).map((application) => <option key={application.id} value={application.id}>{application.name} ({application.code})</option>)}</select></Field>
              : <Field label="Aplicación de AuthCenter"><input value={`${current?.applicationName ?? ""} (${current?.applicationCode ?? ""})`} readOnly /></Field>}
            <Field label="Nombre" error={errors.name?.message} help="Ej.: CRM corporativo"><input {...form.register("name")} autoComplete="off" /></Field>
            <Field label="Entity ID" error={errors.entityId?.message} help="Ej.: https://crm.example.com/saml"><input {...form.register("entityId")} className="mono" autoComplete="off" spellCheck={false} /></Field>
          </div>
          {!create ? <div className="checkbox-grid"><label className="checkbox-field"><input type="checkbox" {...form.register("isActive")} /><span>Aplicación activa: acepta solicitudes de inicio de sesión</span></label></div> : null}
        </section>
        <section className="settings-panel" aria-labelledby="saml-endpoints">
          <div className="settings-panel__heading"><div><h2 id="saml-endpoints">Endpoints de la aplicación</h2><p>AuthCenter sólo publica respuestas en las URLs registradas (binding HTTP-POST).</p></div></div>
          <div className="form-grid">
            <Field label="URLs de ACS (Assertion Consumer Service)" error={errors.assertionConsumerServiceUrls?.message} help="Una por línea; la primera es la predeterminada."><textarea {...form.register("assertionConsumerServiceUrls")} className="mono" rows={3} spellCheck={false} /></Field>
            <Field label="URL de cierre de sesión (SLO)" error={errors.singleLogoutServiceUrl?.message} help="Opcional. Recibe la respuesta cuando la aplicación inicia el cierre de sesión."><input {...form.register("singleLogoutServiceUrl")} className="mono" autoComplete="off" spellCheck={false} /></Field>
          </div>
        </section>
        <section className="settings-panel" aria-labelledby="saml-assertion">
          <div className="settings-panel__heading"><div><h2 id="saml-assertion">Aserción</h2><p>La aserción siempre va firmada con el certificado de AuthCenter.</p></div></div>
          <div className="form-grid">
            <Field label="Formato de NameID" error={errors.nameIdFormat?.message}><select {...form.register("nameIdFormat")}>{nameIdFormats.map((format) => <option key={format} value={format}>{nameIdFormatLabels[format]}</option>)}</select></Field>
            <Field label="Vigencia de la aserción (minutos)" error={errors.assertionLifetimeMinutes?.message}><input type="number" min={1} max={60} {...form.register("assertionLifetimeMinutes", { valueAsNumber: true })} /></Field>
          </div>
          <div className="checkbox-grid">
            <label className="checkbox-field"><input type="checkbox" {...form.register("signResponse")} /><span>Firmar también la respuesta completa</span></label>
            <label className="checkbox-field"><input type="checkbox" {...form.register("encryptAssertions")} /><span>Cifrar la aserción para la aplicación</span></label>
          </div>
          <Field label="Certificado de cifrado de la aplicación" error={errors.encryptionCertificate?.message} help="PEM o base64. Obligatorio si la aserción se cifra."><textarea {...form.register("encryptionCertificate")} className="mono" rows={4} spellCheck={false} /></Field>
          {current?.encryptionCertificate ? <CertificateSummary certificate={current.encryptionCertificate} /> : null}
        </section>
        <section className="settings-panel" aria-labelledby="saml-signing">
          <div className="settings-panel__heading"><div><h2 id="saml-signing">Solicitudes firmadas</h2><p>Con el certificado de firma, AuthCenter verifica las solicitudes firmadas de la aplicación; una firma presente siempre se verifica.</p></div></div>
          <div className="checkbox-grid"><label className="checkbox-field"><input type="checkbox" {...form.register("requireSignedRequests")} /><span>Exigir que las solicitudes vengan firmadas</span></label></div>
          <Field label="Certificado de firma de la aplicación" error={errors.signingCertificate?.message} help="PEM o base64."><textarea {...form.register("signingCertificate")} className="mono" rows={4} spellCheck={false} /></Field>
          {current?.signingCertificate ? <CertificateSummary certificate={current.signingCertificate} /> : null}
        </section>
        <section className="settings-panel" aria-labelledby="saml-attributes">
          <div className="settings-panel__heading">
            <div>
              <h2 id="saml-attributes">Atributos</h2>
              <p>Cada atributo de la aserción y de dónde sale su valor. Los roles y permisos son los de la aplicación de AuthCenter. Ej.: un atributo «email» con el correo de la persona.</p>
            </div>
            {canWrite ? <button className="button button--small button--secondary" type="button" onClick={() => attributes.append({ name: "", source: "email" })} disabled={attributes.fields.length >= 30}>Agregar atributo</button> : null}
          </div>
          {errors.attributes?.message ? <p className="field-error">{errors.attributes.message}</p> : null}
          {attributes.fields.length === 0 ? <p className="muted">La aserción sólo llevará el NameID.</p> : null}
          <ol className="scope-list">
            {attributes.fields.map((field, index) => <li className="scope-row" key={field.id}>
              <Field label={<>Nombre del atributo<span className="sr-only"> {index + 1}</span></>} error={errors.attributes?.[index]?.name?.message}><input {...form.register(`attributes.${index}.name`)} className="mono" autoComplete="off" spellCheck={false} /></Field>
              <Field label={<>Origen<span className="sr-only"> del atributo {index + 1}</span></>} error={errors.attributes?.[index]?.source?.message}><select {...form.register(`attributes.${index}.source`)}>{sources.map((source) => <option key={source.value} value={source.value}>{source.label}</option>)}</select></Field>
              {canWrite ? <button className="button button--small button--danger-quiet" type="button" onClick={() => attributes.remove(index)}>Quitar<span className="sr-only"> el atributo {index + 1}</span></button> : null}
            </li>)}
          </ol>
        </section>
        <section className="settings-panel" aria-labelledby="saml-idp-initiated">
          <div className="settings-panel__heading"><div><h2 id="saml-idp-initiated">Inicio desde AuthCenter</h2><p>Permite abrir la aplicación desde el portal de AuthCenter sin que ella envíe una solicitud.</p></div></div>
          <div className="checkbox-grid"><label className="checkbox-field"><input type="checkbox" {...form.register("allowIdpInitiated")} /><span>Permitir el inicio desde AuthCenter</span></label></div>
          <div className="form-grid">
            <Field label="RelayState predeterminado" error={errors.defaultRelayState?.message} help="Opcional: la página de la aplicación a la que llega el usuario."><input {...form.register("defaultRelayState")} autoComplete="off" /></Field>
            {current?.launchUrl ? <Field label="Enlace de inicio"><input value={`${window.location.origin}${current.launchUrl}`} readOnly className="mono" /></Field> : null}
          </div>
        </section>
      </fieldset>
      {canWrite ? <div className="form-footer"><Link className="button button--secondary" to="/saml-apps">Cancelar</Link><button className="button" type="submit" disabled={save.isPending}>{save.isPending ? "Guardando…" : create ? "Crear aplicación SAML" : "Guardar cambios"}</button></div> : null}
    </form>
    <IdentityProviderPanel />
    {current && canWrite ? <section className="settings-panel settings-panel--actions" aria-labelledby="saml-danger">
      <div className="settings-panel__heading"><div><h2 id="saml-danger">Eliminar aplicación SAML</h2><p>Sus usuarios ya no podrán iniciar sesión en ella con AuthCenter. Para pausarla sin perder la configuración, desactívala.</p></div></div>
      {remove.error ? <p className="alert alert--error" role="alert">{errorMessage(remove.error, SAML_ERRORS)}</p> : null}
      <div className="button-group"><button className="button button--danger-quiet" type="button" onClick={() => { remove.reset(); setConfirmDelete(true); }}>Eliminar aplicación SAML</button></div>
    </section> : null}
    <ConfirmDialog open={confirmDelete} title="Eliminar aplicación SAML" detail={`Se eliminará ${current?.name ?? ""} (${current?.entityId ?? ""}). Esta acción queda auditada y no se puede deshacer.`} confirmLabel="Eliminar" dangerous busy={remove.isPending} error={remove.error} onCancel={() => setConfirmDelete(false)} onConfirm={() => remove.mutate()} />
  </>;
}

function CertificateSummary({ certificate }: { certificate: SamlCertificate }) {
  // Read once when shown: a render must not depend on the clock.
  const [now] = useState(() => Date.now());
  const expired = new Date(certificate.notAfter).valueOf() < now;
  return <dl className="profile-summary">
    <div><dt>Sujeto</dt><dd>{certificate.subject}</dd></div>
    <div><dt>Huella SHA-256</dt><dd className="mono">{certificate.thumbprintSha256}</dd></div>
    <div><dt>Vigente hasta</dt><dd>{formatDate(certificate.notAfter)}{expired ? " (vencido)" : ""}</dd></div>
  </dl>;
}
