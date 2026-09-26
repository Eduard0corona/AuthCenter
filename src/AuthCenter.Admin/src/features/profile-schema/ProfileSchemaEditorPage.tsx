import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ProfileAttributeDefinition } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { Field } from "../../components/Field";
import { HistoryLink } from "../../components/HistoryLink";
import { SaveError } from "../../components/SaveError";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { StatusBadge } from "../../components/StatusBadge";
import { DATA_TYPES, newProfileAttributeSchema, profileAttributeDefaults, profileAttributePayload, profileAttributeSchema, type ProfileAttributeFormValues } from "./profile-schema";

const SCHEMA_ERRORS: Record<string, string> = {
  INVALID_PROFILE_ATTRIBUTE_KEY: "La clave debe tener de 2 a 100 caracteres en minúsculas y no puede ser un campo integrado.",
  PROFILE_ATTRIBUTE_KEY_TAKEN: "Ya existe un atributo con esa clave.",
  PROFILE_SCHEMA_LIMIT_REACHED: "El esquema admite como máximo 100 atributos personalizados.",
  PROFILE_EXISTING_VALUES_INVALID: "El cambio invalidaría valores que ya tienen algunos perfiles. Actualiza esos perfiles antes de endurecer la definición.",
  PROFILE_REQUIRED_DEFAULT_MISSING: "Un atributo obligatorio necesita un valor predeterminado válido.",
  PROFILE_ATTRIBUTE_NOT_FOUND: "El atributo no existe."
};

export default function ProfileSchemaEditorRoute({ create = false }: { create?: boolean }) {
  const { definitionId = "" } = useParams();
  return <ProfileSchemaEditorPage key={create ? "new" : definitionId} create={create} />;
}

function ProfileSchemaEditorPage({ create }: { create: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_PROFILE_SCHEMAS_WRITE");
  const { definitionId = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState("");
  const [confirmDeactivate, setConfirmDeactivate] = useState(false);
  // The API lists the schema; the definition is picked from it (inactive ones included).
  const schema = useQuery({
    queryKey: ["profile-schema", "all"],
    enabled: !create,
    queryFn: ({ signal }) => apiRequest<ProfileAttributeDefinition[]>("/api/profile-schema?includeInactive=true", { signal })
  });
  const current = schema.data?.find((definition) => definition.id === definitionId);
  const form = useForm<ProfileAttributeFormValues>({ resolver: zodResolver(create ? newProfileAttributeSchema : profileAttributeSchema), defaultValues: profileAttributeDefaults() });
  const { reset } = form;
  useEffect(() => { if (current) reset(profileAttributeDefaults(current)); }, [current, reset]);
  const dataType = useWatch({ control: form.control, name: "dataType" });

  const save = useMutation({
    mutationFn: (values: ProfileAttributeFormValues) => create
      ? apiRequest<ProfileAttributeDefinition>("/api/profile-schema", { method: "POST", body: JSON.stringify(profileAttributePayload(values, true)) })
      : apiRequest<ProfileAttributeDefinition>(`/api/profile-schema/${definitionId}`, { method: "PUT", body: JSON.stringify({ ...profileAttributePayload(values, false), version: current?.version }) }),
    onSuccess: async (definition) => {
      await queryClient.invalidateQueries({ queryKey: ["profile-schema"] });
      if (create) { navigate(`/profile-schema/${definition.id}`, { replace: true }); return; }
      setFeedback("Atributo guardado.");
    }
  });
  const deactivate = useMutation({
    mutationFn: () => apiRequest<void>(`/api/profile-schema/${definitionId}`, { method: "DELETE" }),
    onSuccess: async () => {
      setConfirmDeactivate(false);
      setFeedback("Atributo desactivado. Los valores existentes se conservan y puedes reactivarlo cuando quieras.");
      await queryClient.invalidateQueries({ queryKey: ["profile-schema"] });
    }
  });

  if (!create && schema.isPending) return <PageState title="Cargando atributo" busy />;
  if (!create && schema.isError) return <PageState title="No pudimos cargar el atributo" detail={errorMessage(schema.error)} tone="error" action={<Link className="button" to="/profile-schema">Volver</Link>} />;
  if (!create && !current) return <PageState title="Atributo no encontrado" detail="El atributo no existe o fue eliminado." tone="error" action={<Link className="button" to="/profile-schema">Volver</Link>} />;
  const title = create ? "Nuevo atributo" : current?.displayName ?? "Atributo";
  const errors = form.formState.errors;
  const isText = dataType === "String";
  const isNumber = dataType === "Integer" || dataType === "Decimal";
  const valuePlaceholder = placeholders[dataType];

  return <>
    <Breadcrumbs items={[{ label: "Esquema de perfil", to: "/profile-schema" }, { label: title }]} />
    <PageHeader
      eyebrow={create ? "Alta" : current?.key ?? "Directorio"}
      title={title}
      description={create ? "Define una clave estable; el resto de la definición puede cambiar mientras no invalide los valores existentes." : canWrite ? "Ajusta la definición. AuthCenter rechaza cambios que invalidarían valores ya guardados." : "Consulta la definición. Tu acceso es de sólo lectura."}
      actions={<>{create ? null : <HistoryLink entityName="UserProfileAttributeDefinition" entityId={definitionId} />}<Link className="button button--secondary" to="/profile-schema">Volver al listado</Link></>}
    />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    <SaveError error={save.error} messages={SCHEMA_ERRORS} onReload={() => { save.reset(); void schema.refetch().then((fresh) => { const loaded = fresh.data?.find((definition) => definition.id === definitionId); if (loaded) form.reset(profileAttributeDefaults(loaded)); }); }} />
    {deactivate.error ? <p className="alert alert--error" role="alert">{errorMessage(deactivate.error, SCHEMA_ERRORS)}</p> : null}
    <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => { setFeedback(""); return save.mutateAsync(values); })(event)}>
      <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="attribute-identity">
          <div className="settings-panel__heading"><div><h2 id="attribute-identity">Identidad</h2><p>La clave identifica el atributo en SCIM, reglas y tokens; no cambia después de crearlo.</p></div>{current ? <StatusBadge active={current.isActive} activeLabel="Activo" inactiveLabel="Desactivado" /> : null}</div>
          <div className="form-grid">
            <Field label="Clave" error={errors.key?.message} help={create ? "Minúsculas, números, punto, guion o guion bajo. Ej.: cost_center" : undefined}><input {...form.register("key")} readOnly={!create} autoComplete="off" spellCheck={false} placeholder="cost_center" /></Field>
            <Field label="Nombre visible" error={errors.displayName?.message}><input {...form.register("displayName")} autoComplete="off" placeholder="Centro de costos" /></Field>
            <Field label="Tipo de dato" error={errors.dataType?.message} help={create ? undefined : "Cambiar el tipo sólo es posible si los valores existentes siguen siendo válidos."}><select {...form.register("dataType")}>{DATA_TYPES.map((type) => <option key={type.value} value={type.value}>{type.label}</option>)}</select></Field>
            <Field label="Descripción" error={errors.description?.message}><input {...form.register("description")} autoComplete="off" /></Field>
          </div>
          <div className="checkbox-grid">
            <label className="checkbox-field"><input type="checkbox" {...form.register("isRequired")} /><span>Obligatorio en todos los perfiles</span></label>
            {!create ? <label className="checkbox-field"><input type="checkbox" {...form.register("isActive")} /><span>Atributo activo</span></label> : null}
          </div>
        </section>
        <section className="settings-panel" aria-labelledby="attribute-rules">
          <div className="settings-panel__heading"><div><h2 id="attribute-rules">Valores y validación</h2><p>Las reglas se aplican al editar un perfil, a SCIM y a los profile mappings.</p></div></div>
          <div className="form-grid">
            {dataType === "Boolean"
              ? <Field label="Valor predeterminado" error={errors.defaultValue?.message}><select {...form.register("defaultValue")}><option value="">Sin valor</option><option value="true">Sí</option><option value="false">No</option></select></Field>
              : <Field label="Valor predeterminado" error={errors.defaultValue?.message} help="Se usa en los perfiles sin valor; obligatorio si el atributo es obligatorio."><input {...form.register("defaultValue")} type={dataType === "Date" ? "date" : dataType === "DateTime" ? "datetime-local" : "text"} inputMode={isNumber ? "decimal" : undefined} placeholder={valuePlaceholder} /></Field>}
            {isText ? <>
              <Field label="Longitud mínima" error={errors.minLength?.message}><input {...form.register("minLength")} inputMode="numeric" /></Field>
              <Field label="Longitud máxima" error={errors.maxLength?.message}><input {...form.register("maxLength")} inputMode="numeric" /></Field>
              <Field label="Patrón (expresión regular)" error={errors.validationPattern?.message} help="Sin retroceso (non-backtracking). Ej.: ^[A-Z]{2}-\d{4}$"><input {...form.register("validationPattern")} className="mono" spellCheck={false} /></Field>
            </> : null}
            {isNumber ? <>
              <Field label="Mínimo" error={errors.minimumNumber?.message}><input {...form.register("minimumNumber")} inputMode="decimal" /></Field>
              <Field label="Máximo" error={errors.maximumNumber?.message}><input {...form.register("maximumNumber")} inputMode="decimal" /></Field>
            </> : null}
          </div>
          {dataType !== "Boolean" ? <Field label="Valores permitidos" error={errors.allowedValues?.message} help="Uno por línea (máximo 100). Déjalo vacío para aceptar cualquier valor que cumpla las demás reglas."><textarea {...form.register("allowedValues")} rows={5} placeholder={valuePlaceholder} /></Field> : null}
        </section>
      </fieldset>
      {canWrite ? <div className="form-footer">
        {!create && current?.isActive ? <button className="button button--danger-quiet" type="button" onClick={() => setConfirmDeactivate(true)}>Desactivar atributo</button> : null}
        <Link className="button button--secondary" to="/profile-schema">Cancelar</Link>
        <button className="button" type="submit" disabled={save.isPending}>{save.isPending ? "Guardando…" : create ? "Crear atributo" : "Guardar cambios"}</button>
      </div> : null}
    </form>
    <ConfirmDialog open={confirmDeactivate} title="Desactivar atributo" detail={`"${title}" dejará de mostrarse y de validarse en los perfiles, SCIM y reglas nuevas. Los valores guardados se conservan.`} confirmLabel="Desactivar" dangerous busy={deactivate.isPending} error={deactivate.error} onCancel={() => setConfirmDeactivate(false)} onConfirm={() => deactivate.mutate()} />
  </>;
}

const placeholders: Record<ProfileAttributeFormValues["dataType"], string> = {
  String: "Operaciones",
  Integer: "3",
  Decimal: "2.5",
  Boolean: "",
  Date: "2026-01-31",
  DateTime: ""
};
