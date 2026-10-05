import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { lazy, Suspense, useEffect, useState } from "react";
import { useForm, type UseFormRegisterReturn } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationBranding, ApplicationSummary, RoleSummary } from "../../api/types";
import { askForPermission, needPermission } from "../../auth/permissions";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { Field } from "../../components/Field";
import { HistoryLink } from "../../components/HistoryLink";
import { SaveError } from "../../components/SaveError";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { StatusBadge } from "../../components/StatusBadge";
import { ApplicationGovernancePanel } from "../governance/ApplicationGovernancePanel";
import { ApplicationIntegrationsPanel } from "./ApplicationIntegrationsPanel";
import { applicationDefaults, applicationPayload, applicationSchema, type ApplicationFormValues } from "./application";
import type { BrandingFormValues } from "./branding";

const BrandingDialog = lazy(() => import("./BrandingDialog").then((module) => ({ default: module.BrandingDialog })));

export default function ApplicationEditorPage({ create = false }: { create?: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_APPLICATIONS_WRITE");
  const canReadRoles = permissions.has("AUTHCENTER_ROLES_READ");
  const { applicationId } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState("");
  const [brandingOpen, setBrandingOpen] = useState(false);
  const [confirmStatus, setConfirmStatus] = useState(false);
  const application = useQuery({
    queryKey: ["application", applicationId],
    enabled: !create && Boolean(applicationId),
    queryFn: ({ signal }) => apiRequest<ApplicationSummary>(`/api/applications/${applicationId}`, { signal })
  });
  const form = useForm<ApplicationFormValues>({ resolver: zodResolver(applicationSchema), defaultValues: applicationDefaults() });
  const availableRoles = useQuery({
    queryKey: ["roles", "application-default", applicationId],
    enabled: !create && canReadRoles && Boolean(applicationId),
    queryFn: ({ signal }) => fetchAllAsPage<RoleSummary>(`/api/roles?applicationSystemId=${encodeURIComponent(applicationId ?? "")}`, signal)
  });

  useEffect(() => {
    if (application.data) form.reset(applicationDefaults(application.data));
  }, [application.data, form]);

  const save = useMutation({
    mutationFn: (values: ApplicationFormValues) => {
      const current = application.data;
      const path = create ? "/api/applications" : `/api/applications/${applicationId}`;
      return apiRequest<ApplicationSummary>(path, {
        method: create ? "POST" : "PUT",
        body: JSON.stringify(applicationPayload(values, current))
      });
    },
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: ["applications"] });
      queryClient.setQueryData(["application", saved.id], saved);
      if (create) {
        navigate(`/applications/${saved.id}`, { replace: true });
        return;
      }
      form.reset(applicationDefaults(saved));
      setFeedback("La configuración de la aplicación quedó actualizada.");
    }
  });
  const updateBranding = useMutation({
    mutationFn: (values: BrandingFormValues) => apiRequest<ApplicationBranding>(`/api/applications/${applicationId}/branding`, {
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
      setBrandingOpen(false);
      setFeedback("La marca quedó guardada.");
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["application", applicationId] }),
        queryClient.invalidateQueries({ queryKey: ["applications"] })
      ]);
    }
  });
  const changeStatus = useMutation({
    mutationFn: () => apiRequest<void>(`/api/applications/${applicationId}/${application.data?.isActive ? "deactivate" : "activate"}`, { method: "PATCH" }),
    onSuccess: async () => {
      setConfirmStatus(false);
      setFeedback(application.data?.isActive ? "La aplicación quedó desactivada." : "La aplicación quedó activa.");
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["application", applicationId] }),
        queryClient.invalidateQueries({ queryKey: ["applications"] })
      ]);
    }
  });

  if (!create && application.isPending) return <PageState title="Cargando aplicación" busy />;
  if (!create && application.isError) return <PageState title="No pudimos cargar la aplicación" detail={errorMessage(application.error)} tone="error" action={<Link className="button" to="/applications">Volver</Link>} />;

  const current = application.data;
  const currentDefaultRoleId = current?.registrationSettings?.defaultRoleId ?? null;
  const currentRoleListed = availableRoles.data?.items.some((role) => role.id === currentDefaultRoleId) ?? false;
  const title = create ? "Nueva aplicación" : current?.name ?? "Aplicación";
  return (
    <>
      <Breadcrumbs items={[{ label: "Aplicaciones", to: "/applications" }, { label: title }]} />
      <PageHeader
        eyebrow={create ? "Alta" : current?.code ?? "Aplicaciones"}
        title={title}
        description={create ? "Registra la aplicación y decide quién puede crear una cuenta y cómo inicia sesión. Después le conectarás un cliente OAuth o SAML." : canWrite ? "Decide quién puede crear una cuenta y cómo inicia sesión. Desde aquí llegas a sus clientes, a las personas con acceso y a su política." : "Consulta la configuración. Tu acceso actual es de sólo lectura."}
        actions={<>{create ? null : <HistoryLink entityName="ApplicationSystem" entityId={current?.id} />}<Link className="button button--secondary" to="/applications">Volver al listado</Link></>}
      />
      {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
      <SaveError error={save.error} onReload={() => { save.reset(); void application.refetch().then((fresh) => { if (fresh.data) form.reset(applicationDefaults(fresh.data)); }); }} />
      {current ? <ApplicationIntegrationsPanel application={current} /> : null}
      <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => save.mutateAsync(values))(event)}>
        <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="application-identity">
          <div className="settings-panel__heading"><div><h2 id="application-identity">Identidad</h2><p>El código es estable y se utiliza en tokens, SDKs e integraciones.</p></div>{current ? <StatusBadge active={current.isActive} activeLabel="Activa" inactiveLabel="Inactiva" /> : null}</div>
          <div className="form-grid">
            <Field label="Código" error={form.formState.errors.code?.message}><input {...form.register("code")} disabled={!create} autoComplete="off" /></Field>
            <Field label="Nombre" error={form.formState.errors.name?.message}><input {...form.register("name")} autoComplete="organization" /></Field>
          </div>
          <Field label="Descripción" error={form.formState.errors.description?.message}><textarea {...form.register("description")} rows={3} /></Field>
        </section>

        <section className="settings-panel" aria-labelledby="registration-policy">
          <div className="settings-panel__heading"><div><h2 id="registration-policy">Registro y acceso</h2><p>Quién puede crear una cuenta, con qué métodos inicia sesión y qué verificación se le pide.</p></div></div>
          <div className="form-grid">
            <Field label="Modo de registro" error={form.formState.errors.registrationMode?.message}>
              <select {...form.register("registrationMode")}>
                <option value="Closed">Cerrado</option><option value="Open">Abierto</option><option value="InviteOnly">Sólo invitación</option><option value="ApprovalRequired">Requiere aprobación</option>
              </select>
            </Field>
            <Field label="Dominios permitidos" error={form.formState.errors.allowedEmailDomains?.message} help="Separados por coma; vacío permite cualquier dominio. Ej.: empresa.com, filial.mx"><input {...form.register("allowedEmailDomains")} autoComplete="off" /></Field>
          </div>
          {!create && canReadRoles ? <Field label="Rol predeterminado" error={undefined} help="Se asignará automáticamente después del registro aprobado."><select {...form.register("defaultRoleId", { setValueAs: (value) => value || null })}><option value="">Sin rol predeterminado</option>{currentDefaultRoleId && !currentRoleListed ? <option value={currentDefaultRoleId}>Rol actual</option> : null}{availableRoles.data?.items.filter((role) => role.isActive).map((role) => <option key={role.id} value={role.id}>{role.name}</option>)}</select></Field> : null}
          {!create && !canReadRoles ? <p className="alert alert--info">El rol predeterminado actual se conservará. {needPermission("AUTHCENTER_ROLES_READ", "cambiarlo")}</p> : null}
          {create ? <p className="alert alert--info">Crea primero la aplicación; después podrás asignar uno de sus roles como predeterminado.</p> : null}
          <fieldset className="check-group"><legend>Métodos de autenticación</legend><div className="checkbox-grid">
            <Checkbox label="Contraseña" registration={form.register("allowPasswordLogin")} />
            <Checkbox label="Enlace de acceso por correo" registration={form.register("allowMagicLink")} />
            <Checkbox label="Google" registration={form.register("allowGoogleLogin")} />
            <Checkbox label="Microsoft" registration={form.register("allowMicrosoftLogin")} />
            <Checkbox label="GitHub" registration={form.register("allowGitHubLogin")} />
            <Checkbox label="Apple" registration={form.register("allowAppleLogin")} />
          </div>{form.formState.errors.allowPasswordLogin ? <p className="field-error">{form.formState.errors.allowPasswordLogin.message}</p> : null}</fieldset>
          <fieldset className="check-group"><legend>Requisitos de seguridad</legend><div className="checkbox-grid">
            <Checkbox label="Confirmación de correo" registration={form.register("requireEmailConfirmation")} />
            <Checkbox label="Verificación en dos pasos (MFA) obligatoria" registration={form.register("requireMfa")} />
          </div></fieldset>
        </section>
        </fieldset>
        {canWrite ? <div className="form-footer">
          <Link className="button button--secondary" to="/applications">Cancelar</Link>
          <button className="button" type="submit" disabled={save.isPending}>{save.isPending ? "Guardando…" : create ? "Crear aplicación" : "Guardar configuración"}</button>
        </div> : <p className="muted">{askForPermission("AUTHCENTER_APPLICATIONS_WRITE", "cambiar esta configuración")}</p>}
      </form>

      {current && permissions.has("AUTHCENTER_GOVERNANCE_READ") ? <ApplicationGovernancePanel applicationId={current.id} /> : null}

      {current && canWrite ? <section className="settings-panel settings-panel--actions" aria-labelledby="application-actions">
        <div className="settings-panel__heading"><div><h2 id="application-actions">Marca y estado</h2><p>Cambia la marca que ven sus usuarios al iniciar sesión, o desactiva la aplicación para bloquear el acceso.</p></div></div>
        <div className="button-group"><button className="button button--secondary" type="button" onClick={() => { updateBranding.reset(); setBrandingOpen(true); }}>Editar marca</button><button className="button button--danger-quiet" type="button" disabled={current.code === "AUTHCENTER" || changeStatus.isPending} onClick={() => setConfirmStatus(true)}>{current.isActive ? "Desactivar" : "Activar"}</button></div>
        {current.code === "AUTHCENTER" ? <p className="muted">AUTHCENTER debe permanecer activa para conservar el acceso administrativo.</p> : null}
        {changeStatus.error ? <p className="alert alert--error" role="alert">{errorMessage(changeStatus.error)}</p> : null}
      </section> : null}
      {brandingOpen && current ? <Suspense fallback={<p className="alert" role="status">Cargando el editor de marca…</p>}><BrandingDialog application={current} busy={updateBranding.isPending} error={updateBranding.error ? errorMessage(updateBranding.error) : ""} onClose={() => { if (!updateBranding.isPending) setBrandingOpen(false); }} onSave={async (values) => { await updateBranding.mutateAsync(values); }} /></Suspense> : null}
      <ConfirmDialog open={confirmStatus} title={`${current?.isActive ? "Desactivar" : "Activar"} ${current?.name ?? "aplicación"}`} detail={current?.isActive ? "Los inicios de sesión nuevos quedarán bloqueados y las sesiones activas serán revocadas." : "La aplicación volverá a aceptar accesos según su política."} confirmLabel={current?.isActive ? "Desactivar aplicación" : "Activar aplicación"} dangerous={Boolean(current?.isActive)} busy={changeStatus.isPending} error={changeStatus.error} onCancel={() => setConfirmStatus(false)} onConfirm={() => changeStatus.mutate()} />
    </>
  );
}

function Checkbox({ label, registration }: { label: string; registration: UseFormRegisterReturn }) {
  return <label className="checkbox-field"><input type="checkbox" {...registration} /><span>{label}</span></label>;
}

