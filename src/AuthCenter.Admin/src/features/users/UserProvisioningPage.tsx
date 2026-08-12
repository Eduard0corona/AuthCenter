import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { Link } from "react-router-dom";
import { apiRequest, ApiError } from "../../api/client";
import type { ApplicationSummary, PagedResult, RoleSummary, UserSummary } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { generateTemporaryPassword, userProvisioningDefaults, userProvisioningPayload, userProvisioningSchema, type UserProvisioningForm, type UserProvisioningMode } from "./provisioning";

export default function UserProvisioningPage({ mode }: { mode: UserProvisioningMode }) {
  const { permissions } = useSession();
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const canReadRoles = permissions.has("AUTHCENTER_ROLES_READ");
  const queryClient = useQueryClient();
  const [created, setCreated] = useState<UserSummary | null>(null);
  const [feedback, setFeedback] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const form = useForm<UserProvisioningForm>({ resolver: zodResolver(userProvisioningSchema(mode)), defaultValues: userProvisioningDefaults(mode) });
  const applicationId = useWatch({ control: form.control, name: "applicationSystemId" });
  const watchedGrantApplicationAccess = useWatch({ control: form.control, name: "grantApplicationAccess" });
  const grantApplicationAccess = mode === "invite" || watchedGrantApplicationAccess;
  const applications = useQuery({
    queryKey: ["applications", "user-provisioning"],
    enabled: canReadApplications,
    queryFn: ({ signal }) => apiRequest<PagedResult<ApplicationSummary>>("/api/applications?page=1&pageSize=100&isActive=true", { signal })
  });
  const roles = useQuery({
    queryKey: ["roles", "user-provisioning", applicationId],
    enabled: canReadRoles && Boolean(applicationId),
    queryFn: ({ signal }) => apiRequest<PagedResult<RoleSummary>>(`/api/roles?page=1&pageSize=100&isActive=true&applicationSystemId=${encodeURIComponent(applicationId)}`, { signal })
  });
  const save = useMutation({
    mutationFn: (values: UserProvisioningForm) => apiRequest<UserSummary>(mode === "invite" ? "/api/users/invitations" : "/api/users", {
      method: "POST",
      body: JSON.stringify(userProvisioningPayload(mode, values))
    }),
    onSuccess: async (user) => {
      setCreated(user);
      setFeedback(mode === "invite" ? "La invitación se envió sin exponer su token en la consola." : "El usuario fue creado y deberá reemplazar la contraseña temporal al iniciar sesión.");
      setShowPassword(false);
      form.reset(userProvisioningDefaults(mode));
      await queryClient.invalidateQueries({ queryKey: ["users"] });
    }
  });
  const title = mode === "invite" ? "Invitar usuario" : "Crear usuario";
  const selectedApplication = applications.data?.items.find((application) => application.id === applicationId);

  if (applications.isError) return <PageState title="No pudimos cargar las aplicaciones" detail={message(applications.error)} tone="error" action={<button className="button" onClick={() => void applications.refetch()}>Reintentar</button>} />;
  return <>
    <Breadcrumbs items={[{ label: "Usuarios", to: "/users" }, { label: title }]} />
    <PageHeader eyebrow="Directorio" title={title} description={mode === "invite" ? "Envía un vínculo de un solo uso y concede únicamente el acceso inicial necesario." : "Crea una identidad local con una contraseña temporal que deberá cambiarse en el primer acceso."} actions={<Link className="button button--secondary" to="/users">Volver al listado</Link>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback} {created ? <Link to={`/users/${created.id}`}>Administrar {created.fullName}</Link> : null}</p> : null}
    {save.error ? <p className="alert alert--error" role="alert">{message(save.error)}</p> : null}
    {!canReadApplications ? <p className="alert alert--info">Necesitas AUTHCENTER_APPLICATIONS_READ para seleccionar acceso inicial. Puedes crear un usuario sin acceso, pero no enviar invitaciones.</p> : null}
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => save.mutateAsync(values))(event)}>
      <fieldset className="settings-fieldset" disabled={save.isPending || (mode === "invite" && !canReadApplications)}>
        <section className="settings-panel" aria-labelledby="provisioning-identity">
          <div className="settings-panel__heading"><div><h2 id="provisioning-identity">Identidad</h2><p>El correo será el identificador único de inicio de sesión.</p></div></div>
          <div className="form-grid">
            <Field label="Nombre completo" error={form.formState.errors.fullName?.message}><input autoComplete="name" {...form.register("fullName")} /></Field>
            <Field label="Correo" error={form.formState.errors.email?.message}><input type="email" autoComplete="email" {...form.register("email")} /></Field>
          </div>
          {mode === "create" ? <div className="field"><label htmlFor="temporary-password">Contraseña temporal</label><input id="temporary-password" type={showPassword ? "text" : "password"} autoComplete="new-password" aria-describedby="temporary-password-help" {...form.register("password")} /><small className="field-help" id="temporary-password-help">Se mantiene sólo en este formulario. Cópiala por un canal seguro antes de crear; AuthCenter exigirá reemplazarla en el primer inicio de sesión.</small>{form.formState.errors.password ? <small className="field-error">{form.formState.errors.password.message}</small> : null}<span className="button-group"><button className="button button--small button--secondary" type="button" onClick={() => form.setValue("password", generateTemporaryPassword(), { shouldDirty: true, shouldValidate: true })}>Generar contraseña segura</button><button className="button button--small button--secondary" type="button" aria-controls="temporary-password" onClick={() => setShowPassword((visible) => !visible)}>{showPassword ? "Ocultar contraseña" : "Mostrar contraseña"}</button></span></div> : null}
        </section>
        <section className="settings-panel" aria-labelledby="provisioning-access">
          <div className="settings-panel__heading"><div><h2 id="provisioning-access">Acceso inicial</h2><p>{mode === "invite" ? "La aplicación define el destino del vínculo de invitación." : "Puedes dejar la identidad sin aplicaciones y asignarlas después desde su detalle."}</p></div></div>
          {mode === "create" ? <label className="checkbox-field"><input type="checkbox" {...form.register("grantApplicationAccess")} onChange={(event) => { form.setValue("grantApplicationAccess", event.target.checked); if (!event.target.checked) { form.setValue("applicationSystemId", ""); form.setValue("roleIds", []); } }} /><span>Conceder acceso a una aplicación ahora</span></label> : null}
          {grantApplicationAccess ? <>
            <Field label="Aplicación" error={form.formState.errors.applicationSystemId?.message}><select {...form.register("applicationSystemId")} onChange={(event) => { form.setValue("applicationSystemId", event.target.value, { shouldValidate: true }); form.setValue("roleIds", []); }}><option value="">Selecciona…</option>{applications.data?.items.filter((application) => application.isActive).map((application) => <option key={application.id} value={application.id}>{application.name} ({application.code})</option>)}</select></Field>
            <label className="checkbox-field"><input type="checkbox" {...form.register("applicationAccessIsActive")} /><span>Conceder acceso activo inmediatamente</span></label>
            {applicationId ? <fieldset className="check-group"><legend>Roles directos opcionales en {selectedApplication?.name ?? "la aplicación"}</legend><div className="permission-matrix">{roles.isPending ? <p className="muted">Cargando roles…</p> : roles.data?.items.filter((role) => role.isActive && !role.isSystemRole).length ? roles.data.items.filter((role) => role.isActive && !role.isSystemRole).map((role) => <label className="permission-option" key={role.id}><input type="checkbox" value={role.id} {...form.register("roleIds")} /><span><strong>{role.name}</strong><small>{role.description ?? "Sin descripción"}</small></span></label>) : <p className="muted">No hay roles delegables activos.</p>}</div></fieldset> : null}
          </> : null}
        </section>
      </fieldset>
      <div className="form-footer"><Link className="button button--secondary" to="/users">Cancelar</Link><button className="button" disabled={save.isPending || (mode === "invite" && !canReadApplications)}>{save.isPending ? "Procesando…" : mode === "invite" ? "Enviar invitación" : "Crear usuario"}</button></div>
    </form>
  </>;
}

function Field({ label, error, help, children }: { label: string; error: string | undefined; help?: string | undefined; children: React.ReactNode }) {
  return <label className="field"><span>{label}</span>{children}{help ? <small className="field-help">{help}</small> : null}{error ? <small className="field-error">{error}</small> : null}</label>;
}

function message(error: unknown): string { return error instanceof ApiError ? error.message : "Ocurrió un error inesperado."; }
