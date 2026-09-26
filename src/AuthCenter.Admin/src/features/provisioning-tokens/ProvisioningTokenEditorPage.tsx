import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { useForm, type UseFormRegisterReturn } from "react-hook-form";
import { Link, useNavigate, useParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, ProvisioningTokenCreated, ProvisioningTokenMetadata } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { HistoryLink } from "../../components/HistoryLink";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { ReauthenticationDialog } from "../../components/ReauthenticationDialog";
import { formatDate } from "../../utils/format";
import { SecretRevealDialog } from "../oauth-clients/SecretRevealDialog";
import { defaultExpiration, provisioningScopes, scimBaseUrl, provisioningTokenDefaults, provisioningTokenPayload, provisioningTokenRotationPayload, provisioningTokenRotationSchema, provisioningTokenSchema, type ProvisioningTokenFormValues, type ProvisioningTokenRotationValues } from "./provisioning-token";
import { TokenStatus } from "./ProvisioningTokensPage";
import { ScimDiagnosticsPanel } from "./ScimDiagnosticsPanel";

type SensitiveAction = "rotate" | "revoke" | null;

// One page instance per token: after a rotation navigates to the replacement, no dialog or
// pending action of the previous credential can survive into the next one.
export default function ProvisioningTokenEditorRoute({ create = false }: { create?: boolean }) {
  const { tokenId = "" } = useParams();
  return <ProvisioningTokenEditorPage key={create ? "new" : tokenId} create={create} />;
}

function ProvisioningTokenEditorPage({ create }: { create: boolean }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_PROVISIONING_WRITE");
  const canReadApplications = permissions.has("AUTHCENTER_APPLICATIONS_READ");
  const { tokenId = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState("");
  const [secret, setSecret] = useState("");
  const [revealedTokenId, setRevealedTokenId] = useState("");
  const [sensitiveAction, setSensitiveAction] = useState<SensitiveAction>(null);
  const token = useQuery({
    queryKey: ["provisioning-token", tokenId],
    enabled: !create && Boolean(tokenId),
    queryFn: ({ signal }) => apiRequest<ProvisioningTokenMetadata>(`/api/provisioning-tokens/${tokenId}`, { signal })
  });
  const applications = useQuery({
    queryKey: ["applications", "provisioning-token-editor"],
    enabled: create && canReadApplications,
    queryFn: ({ signal }) => fetchAllAsPage<ApplicationSummary>("/api/applications", signal)
  });
  const form = useForm<ProvisioningTokenFormValues>({ resolver: zodResolver(provisioningTokenSchema), defaultValues: provisioningTokenDefaults() });
  const rotationForm = useForm<ProvisioningTokenRotationValues>({ resolver: zodResolver(provisioningTokenRotationSchema), defaultValues: { expiresAt: defaultExpiration() } });
  const current = token.data;

  const save = useMutation({
    mutationFn: (values: ProvisioningTokenFormValues) => apiRequest<ProvisioningTokenCreated>("/api/provisioning-tokens", { method: "POST", body: JSON.stringify(provisioningTokenPayload(values)) }),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ["provisioning-tokens"] });
      setRevealedTokenId(result.id);
      setSecret(result.token);
    }
  });
  const rotate = useMutation({
    mutationFn: ({ proofToken, values }: { proofToken: string; values: ProvisioningTokenRotationValues }) => apiRequest<ProvisioningTokenCreated>(`/api/provisioning-tokens/${tokenId}/rotate?expiresAt=${encodeURIComponent(provisioningTokenRotationPayload(values))}`, { method: "POST", headers: { "X-AuthCenter-Reauthentication": proofToken } }),
    onSuccess: async (result) => {
      setSensitiveAction(null);
      setRevealedTokenId(result.id);
      setSecret(result.token);
      await queryClient.invalidateQueries({ queryKey: ["provisioning-tokens"] });
    }
  });
  const revoke = useMutation({
    mutationFn: (proofToken: string) => apiRequest<void>(`/api/provisioning-tokens/${tokenId}`, { method: "DELETE", headers: { "X-AuthCenter-Reauthentication": proofToken } }),
    onSuccess: async () => {
      setSensitiveAction(null);
      setFeedback("El provisioning token quedó revocado y ya no puede autenticar solicitudes SCIM.");
      await Promise.all([queryClient.invalidateQueries({ queryKey: ["provisioning-token", tokenId] }), queryClient.invalidateQueries({ queryKey: ["provisioning-tokens"] })]);
    }
  });

  if (!create && token.isPending) return <PageState title="Cargando provisioning token" busy />;
  if (!create && token.isError) return <PageState title="No pudimos cargar el provisioning token" detail={errorMessage(token.error)} tone="error" action={<Link className="button" to="/provisioning-tokens">Volver</Link>} />;
  if (create && !canReadApplications) return <PageState title="No puedes crear provisioning tokens" detail="Necesitas AUTHCENTER_APPLICATIONS_READ para seleccionar la aplicación propietaria." tone="error" action={<Link className="button" to="/provisioning-tokens">Volver</Link>} />;
  const title = create ? "Nuevo provisioning token" : current?.name ?? "Provisioning token";
  const actionError = rotate.error ?? revoke.error;

  function closeSecret(): void {
    setSecret("");
    navigate(`/provisioning-tokens/${revealedTokenId}`, { replace: true });
  }

  return <>
    <Breadcrumbs items={[{ label: "Provisioning tokens", to: "/provisioning-tokens" }, { label: title }]} />
    <PageHeader eyebrow={create ? "Alta" : current?.applicationName ?? "Integraciones"} title={title} description={create ? "Emite una credencial SCIM con el alcance mínimo y una expiración explícita." : canWrite ? "Consulta el uso y rota o revoca la credencial. Su valor original nunca se recupera." : "Consulta los metadatos de la credencial. El secreto no está disponible."} actions={<>{create ? null : <HistoryLink entityName="ProvisioningToken" entityId={tokenId} />}<Link className="button button--secondary" to="/provisioning-tokens">Volver al listado</Link></>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    {save.error ? <p className="alert alert--error" role="alert">{errorMessage(save.error)}</p> : null}
    {create ? <form className="settings-form" onSubmit={(event) => void form.handleSubmit((values) => save.mutateAsync(values))(event)}>
      <fieldset className="settings-fieldset" disabled={!canWrite}>
        <section className="settings-panel" aria-labelledby="provisioning-token-identity">
          <div className="settings-panel__heading"><div><h2 id="provisioning-token-identity">Identidad y vigencia</h2><p>El nombre, la aplicación y los scopes quedan asociados a esta credencial.</p></div></div>
          <div className="form-grid">
            <Field label="Aplicación" error={form.formState.errors.applicationSystemId?.message}><select {...form.register("applicationSystemId")}><option value="">Selecciona una aplicación</option>{applications.data?.items.filter((application) => application.isActive).map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</select></Field>
            <Field label="Nombre" error={form.formState.errors.name?.message}><input {...form.register("name")} autoComplete="off" placeholder="Sincronización de directorio" /></Field>
            <Field label="Expira" error={form.formState.errors.expiresAt?.message} help="Máximo un año; la fecha se enviará en UTC."><input type="datetime-local" {...form.register("expiresAt")} /></Field>
          </div>
          <fieldset className="check-group"><legend>Scopes SCIM</legend><p className="field-help">Concede únicamente las operaciones que usará el integrador.</p><div className="checkbox-grid">{provisioningScopes.map((scope) => <Checkbox key={scope} label={scope} registration={form.register("scopes")} value={scope} />)}</div>{form.formState.errors.scopes ? <p className="field-error">{form.formState.errors.scopes.message}</p> : null}</fieldset>
        </section>
      </fieldset>
      <div className="form-footer"><Link className="button button--secondary" to="/provisioning-tokens">Cancelar</Link><button className="button" type="submit" disabled={!canWrite || save.isPending}>{save.isPending ? "Creando…" : "Crear provisioning token"}</button></div>
    </form> : current ? <>
      <section className="settings-panel" aria-labelledby="provisioning-token-detail">
        <div className="settings-panel__heading"><div><h2 id="provisioning-token-detail">Metadatos</h2><p>Estos datos permiten operar la credencial sin exponer el token original.</p></div><TokenStatus status={current.status} /></div>
        <dl className="profile-summary"><div><dt>Aplicación</dt><dd>{current.applicationName}</dd></div><div><dt>Creado</dt><dd>{formatDate(current.createdAt)}</dd></div><div><dt>Expira</dt><dd>{formatDate(current.expiresAt)}</dd></div><div><dt>Último uso</dt><dd>{formatDate(current.lastUsedAt)}</dd></div><div><dt>URL base SCIM</dt><dd className="mono">{scimBaseUrl()}</dd></div></dl>
        <div><p className="field-help">Scopes efectivos</p><div className="button-group" aria-label="Scopes efectivos">{current.scopes.map((scope) => <span className="tag mono" key={scope}>{scope}</span>)}</div></div>
        <p className="alert alert--info">AuthCenter almacena únicamente el hash de esta credencial; el valor original no puede recuperarse.</p>
      </section>
      <ScimDiagnosticsPanel tokenId={current.id} />
      {canWrite && current.status === "active" ? <section className="settings-panel settings-panel--actions" aria-labelledby="provisioning-token-actions"><div className="settings-panel__heading"><div><h2 id="provisioning-token-actions">Rotación y revocación</h2><p>Ambas acciones requieren reautenticación y consumen una prueba de un solo uso.</p></div></div><div className="form-grid"><Field label="Expiración del reemplazo" error={rotationForm.formState.errors.expiresAt?.message} help="La rotación revoca esta credencial de inmediato."><input type="datetime-local" {...rotationForm.register("expiresAt")} /></Field></div><div className="button-group"><button className="button button--secondary" type="button" onClick={() => void rotationForm.handleSubmit(() => { rotate.reset(); setSensitiveAction("rotate"); })()}>Rotar token</button><button className="button button--danger-quiet" type="button" onClick={() => { revoke.reset(); setSensitiveAction("revoke"); }}>Revocar token</button></div>{actionError ? <p className="alert alert--error" role="alert">{errorMessage(actionError)}</p> : null}</section> : null}
    </> : null}
    <ReauthenticationDialog open={sensitiveAction !== null} purpose={sensitiveAction === "rotate" ? "admin.provisioning-token.rotate" : "admin.provisioning-token.revoke"} title={sensitiveAction === "rotate" ? "Rotar provisioning token" : "Revocar provisioning token"} detail={sensitiveAction === "rotate" ? "La credencial actual dejará de funcionar de inmediato y el reemplazo se mostrará una sola vez." : "La credencial dejará de autenticar solicitudes SCIM de inmediato. Esta acción no se puede deshacer."} confirmLabel={sensitiveAction === "rotate" ? "Verificar y rotar" : "Verificar y revocar"} dangerous={sensitiveAction === "revoke"} onCancel={() => setSensitiveAction(null)} onProof={async (proof) => { if (sensitiveAction === "rotate") await rotate.mutateAsync({ proofToken: proof, values: rotationForm.getValues() }); else if (sensitiveAction === "revoke") await revoke.mutateAsync(proof); }} />
    <SecretRevealDialog open={Boolean(secret)} secret={secret} title={`Token para ${title}`} onClose={closeSecret} />
  </>;
}

function Field({ label, error, help, children }: { label: string; error: string | undefined; help?: string; children: ReactNode }) { return <label className="field"><span>{label}</span>{children}{help ? <span className="field-help">{help}</span> : null}{error ? <span className="field-error">{error}</span> : null}</label>; }
function Checkbox({ label, registration, value }: { label: string; registration: UseFormRegisterReturn; value: string }) { return <label className="checkbox-field"><input type="checkbox" {...registration} value={value} /><span>{label}</span></label>; }
