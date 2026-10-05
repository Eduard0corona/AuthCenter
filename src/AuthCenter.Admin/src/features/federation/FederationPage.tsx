import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest, ApiError } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationSummary, DirectoryGroupSummary, FederationProvider, FederationRouteResult, FederationRoutingRule, ProfileAttributeDefinition } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { ReauthenticationDialog } from "../../components/ReauthenticationDialog";
import { StatusBadge } from "../../components/StatusBadge";
import { describeRoutingRule, moveRule, orderChanged, reorderPayload, routingRulePayload, type RoutingRuleFormValues } from "./federation";
import { RoutingRuleForm } from "./RoutingRuleForm";

type PendingAction =
  | { type: "reorder" }
  | { type: "save-rule"; payload: Record<string, unknown>; rule: FederationRoutingRule | undefined }
  | { type: "delete-rule"; rule: FederationRoutingRule };

export default function FederationPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_FEDERATION_WRITE");
  const canReadGroups = permissions.has("AUTHCENTER_GROUPS_READ");
  const canReadSchema = permissions.has("AUTHCENTER_PROFILE_SCHEMAS_READ");
  const [params, setParams] = useSearchParams();
  const queryClient = useQueryClient();
  const [feedback, setFeedback] = useState("");
  const [editor, setEditor] = useState<"new" | FederationRoutingRule | null>(null);
  const [editorError, setEditorError] = useState("");
  const [pending, setPending] = useState<PendingAction | null>(null);
  // Local reorder is keyed to the server list it was derived from, so a refetch discards stale drafts.
  const [draft, setDraft] = useState<{ source: FederationRoutingRule[]; order: FederationRoutingRule[] } | null>(null);
  const [email, setEmail] = useState("");

  const applications = useQuery({ queryKey: ["applications", "federation"], queryFn: ({ signal }) => fetchAllAsPage<ApplicationSummary>("/api/applications", signal) });
  const applicationId = params.get("applicationId") ?? applications.data?.items.find((application) => application.isActive)?.id ?? "";
  const application = applications.data?.items.find((item) => item.id === applicationId);
  const providers = useQuery({ queryKey: ["federation-providers", applicationId], enabled: Boolean(applicationId), queryFn: ({ signal }) => apiRequest<FederationProvider[]>(`/api/federation/providers?applicationSystemId=${applicationId}`, { signal }) });
  const rules = useQuery({ queryKey: ["federation-routing-rules", applicationId], enabled: Boolean(applicationId), queryFn: ({ signal }) => apiRequest<FederationRoutingRule[]>(`/api/federation/routing-rules?applicationSystemId=${applicationId}`, { signal }) });
  const groups = useQuery({ queryKey: ["groups", "federation"], enabled: canReadGroups, queryFn: ({ signal }) => fetchAllAsPage<DirectoryGroupSummary>("/api/groups?isActive=true", signal) });
  const schema = useQuery({ queryKey: ["profile-schema", "active"], enabled: canReadSchema, queryFn: ({ signal }) => apiRequest<ProfileAttributeDefinition[]>("/api/profile-schema", { signal }) });
  const serverRules = rules.data;
  const order = draft && draft.source === serverRules ? draft.order : serverRules ?? [];
  const setOrder = (update: (current: FederationRoutingRule[]) => FederationRoutingRule[]) => { if (serverRules) setDraft({ source: serverRules, order: update(order) }); };

  const saveRule = useMutation({
    mutationFn: ({ proofToken, payload, rule }: { proofToken: string; payload: Record<string, unknown>; rule: FederationRoutingRule | undefined }) => apiRequest<FederationRoutingRule | void>(rule ? `/api/federation/routing-rules/${rule.id}` : "/api/federation/routing-rules", { method: rule ? "PUT" : "POST", body: JSON.stringify(payload), headers: { "X-AuthCenter-Reauthentication": proofToken } }),
    onSuccess: async (_, variables) => { setPending(null); setEditor(null); setFeedback(variables.rule ? "La regla de enrutamiento quedó guardada." : "La regla de enrutamiento quedó creada."); await queryClient.invalidateQueries({ queryKey: ["federation-routing-rules", applicationId] }); }
  });
  const deleteRule = useMutation({
    mutationFn: ({ proofToken, rule }: { proofToken: string; rule: FederationRoutingRule }) => apiRequest<void>(`/api/federation/routing-rules/${rule.id}`, { method: "DELETE", headers: { "X-AuthCenter-Reauthentication": proofToken } }),
    onSuccess: async () => { setPending(null); setFeedback("La regla de enrutamiento se eliminó."); await queryClient.invalidateQueries({ queryKey: ["federation-routing-rules", applicationId] }); }
  });
  const reorder = useMutation({
    mutationFn: (proofToken: string) => apiRequest<void>("/api/federation/routing-rules/order", { method: "PUT", body: JSON.stringify(reorderPayload(order)), headers: { "X-AuthCenter-Reauthentication": proofToken } }),
    onSuccess: async () => { setPending(null); setFeedback("El orden de evaluación quedó guardado."); await queryClient.invalidateQueries({ queryKey: ["federation-routing-rules", applicationId] }); }
  });
  const simulate = useMutation({
    mutationFn: (value: string) => apiRequest<FederationRouteResult>("/api/federation/route", { method: "POST", body: JSON.stringify({ applicationCode: application?.code ?? "", email: value.trim() }) })
  });

  function selectApplication(value: string): void {
    setEditor(null); setFeedback(""); simulate.reset();
    setParams((current) => { const next = new URLSearchParams(current); if (value) next.set("applicationId", value); else next.delete("applicationId"); return next; });
  }
  function submitRule(values: RoutingRuleFormValues): void {
    const rule = editor === "new" ? undefined : editor ?? undefined;
    const definition = schema.data?.find((item) => item.id === values.profileAttributeDefinitionId);
    const result = routingRulePayload(values, definition, rule);
    if (!result.ok) { setEditorError(result.error); return; }
    setEditorError(""); saveRule.reset();
    setPending({ type: "save-rule", payload: result.payload, rule });
  }
  const dirty = serverRules ? orderChanged(serverRules, order) : false;
  const nextPriority = order.length ? Math.max(...order.map((rule) => rule.priority)) + 10 : 10;
  const groupName = (id: string | null) => groups.data?.items.find((group) => group.id === id)?.name;
  const attributeName = (id: string | null) => schema.data?.find((item) => item.id === id)?.key;
  const actionError = pending ? null : (saveRule.error ?? deleteRule.error ?? reorder.error);
  const conflict = actionError instanceof ApiError && actionError.code === "CONCURRENCY_CONFLICT";

  return <>
    <PageHeader
      eyebrow="Federación"
      title="Proveedores y enrutamiento"
      description="Conecta el proveedor de identidad de una organización (OIDC o SAML) para que su gente entre con su cuenta de trabajo. Las reglas de enrutamiento deciden a quién se envía a cada proveedor."
      actions={canWrite && applicationId ? <Link className="button" to={`/federation/providers/new?applicationId=${applicationId}`}>Nuevo proveedor</Link> : undefined}
    />
    <section className="toolbar" aria-label="Selección de aplicación">
      <label className="field"><span>Aplicación</span><select value={applicationId} onChange={(event) => selectApplication(event.target.value)}><option value="">Selecciona una aplicación</option>{applications.data?.items.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
      <label className="field"><span>Código</span><input value={application?.code ?? ""} readOnly className="mono" /></label>
    </section>
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    {conflict ? <p className="alert alert--error" role="alert">Una regla de enrutamiento cambió desde que la cargaste. Recarga para ver la versión vigente. <button className="button button--small button--secondary" type="button" onClick={() => { saveRule.reset(); reorder.reset(); void rules.refetch(); }}>Recargar</button></p> : actionError ? <p className="alert alert--error" role="alert">{errorMessage(actionError)}</p> : null}
    {!applicationId && applications.data ? <PageState title="Selecciona una aplicación" detail="La federación se configura por aplicación." /> : null}
    {applicationId ? <>
      <section className="settings-panel" aria-labelledby="federation-providers">
        <div className="settings-panel__heading"><div><h2 id="federation-providers">Proveedores de identidad</h2><p>Los secretos y certificados nunca se devuelven; solo su presencia y huella.</p></div></div>
        {providers.isPending ? <PageState title="Cargando proveedores" busy /> : null}
        {providers.isError ? <PageState title="No pudimos cargar los proveedores" detail={errorMessage(providers.error)} tone="error" action={<button className="button" type="button" onClick={() => void providers.refetch()}>Reintentar</button>} /> : null}
        {providers.data && providers.data.length === 0 ? <PageState title="Sin proveedores" detail="Registra el primer IdP corporativo para esta aplicación." /> : null}
        {providers.data?.length ? <div className="data-table" tabIndex={0} role="region" aria-label="Proveedores de identidad, desplazamiento horizontal"><table><caption className="sr-only">Proveedores de identidad</caption><thead><tr><th>Proveedor</th><th>Protocolo</th><th>Emisor (issuer)</th><th>Credenciales</th><th>Cuenta en el primer acceso</th><th>Vinculación</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{providers.data.map((provider) => <tr key={provider.id}><td><strong>{provider.name}</strong></td><td>{provider.protocol === "Saml2" ? "SAML 2.0" : "OIDC"}</td><td><span className="mono cell-detail">{provider.issuer}</span></td><td>{provider.protocol === "Saml2" ? (provider.samlSigningCertificateThumbprint ? <span className="tag mono">certificado {provider.samlSigningCertificateThumbprint.slice(0, 8)}…</span> : <span className="tag tag--warning">Sin certificado</span>) : (provider.hasClientSecret ? <span className="tag">Secreto configurado</span> : <span className="tag tag--warning">Sin secreto</span>)}</td><td>{provider.jitProvisioningEnabled ? "Sí" : "No"}</td><td>{provider.accountLinkingMode === "VerifiedEmail" ? "Correo verificado" : "Deshabilitada"}</td><td><StatusBadge active={provider.isActive} /></td><td className="table-action"><Link className="button button--small button--secondary" to={`/federation/providers/${provider.id}`}>{canWrite ? "Editar" : "Consultar"}</Link></td></tr>)}</tbody></table></div> : null}
      </section>
      <section className="settings-panel settings-panel--actions" aria-labelledby="federation-routing">
        <div className="settings-panel__heading"><div><h2 id="federation-routing">Reglas de enrutamiento</h2><p>Orden de evaluación para la aplicación. Reordena con las flechas y guarda el nuevo orden; te pediremos confirmar tu identidad. En el login, las condiciones de grupo o atributo solo se evalúan para el usuario que ya inició sesión en ese navegador; para cualquier otro correo solo cuenta el dominio, así el login no revela datos del directorio.</p></div>{canWrite && providers.data?.length ? <button className="button button--secondary" type="button" onClick={() => { setEditorError(""); saveRule.reset(); setEditor("new"); }}>Nueva regla</button> : null}</div>
        {rules.isPending ? <PageState title="Cargando reglas de enrutamiento" busy /> : null}
        {rules.isError ? <PageState title="No pudimos cargar las reglas de enrutamiento" detail={errorMessage(rules.error)} tone="error" action={<button className="button" type="button" onClick={() => void rules.refetch()}>Reintentar</button>} /> : null}
        {rules.data && order.length === 0 ? <PageState title="Sin reglas de enrutamiento" detail="Sin reglas, ningún usuario de esta aplicación se enruta a un IdP externo." /> : null}
        {order.length ? <>
          <div className="data-table" tabIndex={0} role="region" aria-label="Reglas de enrutamiento, desplazamiento horizontal"><table><caption className="sr-only">Reglas de enrutamiento en orden de evaluación</caption><thead><tr><th>Orden</th><th>Prioridad</th><th>Proveedor</th><th>Condición</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{order.map((rule, index) => <tr key={rule.id}><td>{index + 1}</td><td>{rule.priority}</td><td><strong>{rule.providerName}</strong></td><td><span className="mono">{describeRoutingRule(rule, groupName(rule.directoryGroupId), attributeName(rule.profileAttributeDefinitionId))}</span></td><td><StatusBadge active={rule.isActive} activeLabel="Activa" inactiveLabel="Inactiva" /></td><td className="table-action"><div className="button-group">{canWrite ? <><button className="button button--small button--secondary" type="button" aria-label={`Subir regla ${index + 1}`} disabled={index === 0} onClick={() => setOrder((current) => moveRule(current, rule.id, -1))}>↑</button><button className="button button--small button--secondary" type="button" aria-label={`Bajar regla ${index + 1}`} disabled={index === order.length - 1} onClick={() => setOrder((current) => moveRule(current, rule.id, 1))}>↓</button><button className="button button--small button--secondary" type="button" onClick={() => { setEditorError(""); saveRule.reset(); setEditor(rule); }}>Editar</button><button className="button button--small button--danger-quiet" type="button" onClick={() => { deleteRule.reset(); setPending({ type: "delete-rule", rule }); }}>Eliminar</button></> : null}</div></td></tr>)}</tbody></table></div>
          {canWrite ? <div className="button-group"><button className="button" type="button" disabled={!dirty} onClick={() => { reorder.reset(); setPending({ type: "reorder" }); }}>Verificar y guardar orden</button>{dirty ? <button className="button button--secondary" type="button" onClick={() => setDraft(null)}>Descartar cambios de orden</button> : null}</div> : null}
        </> : null}
      </section>
      {editor ? <RoutingRuleForm rule={editor === "new" ? undefined : editor} nextPriority={nextPriority} providers={providers.data ?? []} groups={groups.data?.items ?? []} schema={schema.data ?? []} error={editorError} onCancel={() => setEditor(null)} onSave={submitRule} /> : null}
      <section className="settings-panel settings-panel--actions" aria-labelledby="federation-simulation">
        <div className="settings-panel__heading"><div><h2 id="federation-simulation">Simular enrutamiento</h2><p>Comprueba a qué proveedor se enviaría un correo con las reglas guardadas, evaluando todas las condiciones. No inicia ninguna sesión.</p></div></div>
        <form className="form-grid" onSubmit={(event) => { event.preventDefault(); if (email.trim()) simulate.mutate(email); }}>
          <label className="field"><span>Correo de prueba</span><input type="email" value={email} onChange={(event) => setEmail(event.target.value)} autoComplete="off" placeholder="persona@empresa.com" /></label>
          <div className="form-footer"><button className="button button--secondary" type="submit" disabled={simulate.isPending || !email.trim()}>{simulate.isPending ? "Evaluando…" : "Simular"}</button></div>
        </form>
        {simulate.data ? <p className="alert alert--success" role="status">Se enrutaría a <strong>{simulate.data.providerName}</strong> ({simulate.data.protocol === "Saml2" ? "SAML 2.0" : "OIDC"}){simulate.data.matchedRulePriority ? ` por la regla de prioridad ${simulate.data.matchedRulePriority}` : ""}.</p> : null}
        {simulate.error ? <p className="alert alert--info" role="status">{simulate.error instanceof ApiError && simulate.error.status === 404 ? "Ninguna regla activa coincide: el usuario usaría el inicio de sesión local." : errorMessage(simulate.error)}</p> : null}
      </section>
    </> : null}
    <ReauthenticationDialog open={pending !== null} purpose="admin.federation.change" title={pending?.type === "delete-rule" ? "Eliminar regla de enrutamiento" : pending?.type === "reorder" ? "Guardar orden de evaluación" : pending?.rule ? "Guardar regla de enrutamiento" : "Crear regla de enrutamiento"} detail={pending?.type === "delete-rule" ? `Se eliminará la regla ${pending.rule.priority} de ${pending.rule.providerName}. Esta acción queda auditada.` : "Los cambios de enrutamiento afectan de inmediato el inicio de sesión de la aplicación."} confirmLabel={pending?.type === "delete-rule" ? "Verificar y eliminar" : "Verificar y guardar"} dangerous={pending?.type === "delete-rule"} onCancel={() => setPending(null)} onProof={async (proof) => { if (!pending) return; if (pending.type === "reorder") await reorder.mutateAsync(proof); else if (pending.type === "delete-rule") await deleteRule.mutateAsync({ proofToken: proof, rule: pending.rule }); else await saveRule.mutateAsync({ proofToken: proof, payload: pending.payload, rule: pending.rule }); }} />
  </>;
}

