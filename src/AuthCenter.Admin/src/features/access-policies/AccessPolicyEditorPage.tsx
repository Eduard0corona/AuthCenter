import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { fetchAllAsPage } from "../../api/catalog";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { AccessPolicyRule, AccessPolicySimulation, AccessPolicyVersion, ApplicationSummary, DirectoryGroupSummary } from "../../api/types";
import { useSession } from "../../auth/session";
import { Breadcrumbs } from "../../components/Breadcrumbs";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { ReauthenticationDialog } from "../../components/ReauthenticationDialog";
import { StatusBadge } from "../../components/StatusBadge";
import { UserPicker } from "../../components/UserPicker";
import { formatDate } from "../../utils/format";
import { diffPolicyRules, policyRulePayload, type PolicyRuleFormValues } from "./policy";
import { PolicyRuleForm } from "./PolicyRuleForm";

type RuleEditor = AccessPolicyRule | "new" | null;

export default function AccessPolicyEditorPage() {
  const { applicationId = "" } = useParams();
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_ACCESS_POLICIES_WRITE");
  const canReadUsers = permissions.has("AUTHCENTER_USERS_READ");
  const canReadGroups = permissions.has("AUTHCENTER_GROUPS_READ");
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const [editor, setEditor] = useState<RuleEditor>(null);
  const [deleteRule, setDeleteRule] = useState<AccessPolicyRule | null>(null);
  const [publishOpen, setPublishOpen] = useState(false);
  const [feedback, setFeedback] = useState("");
  const [simulationInput, setSimulationInput] = useState({ userId: "", ipAddress: "", evaluatedAtUtc: "", riskLevel: "Unknown", assuranceLevel: "Password" });
  const [simulationUserLabel, setSimulationUserLabel] = useState("");

  const application = useQuery({ queryKey: ["application", applicationId], queryFn: ({ signal }) => apiRequest<ApplicationSummary>(`/api/applications/${applicationId}`, { signal }) });
  const versions = useQuery({ queryKey: ["access-policy-versions", applicationId], queryFn: ({ signal }) => apiRequest<AccessPolicyVersion[]>(`/api/access-policies/applications/${applicationId}/versions`, { signal }) });
  const draft = versions.data?.find((version) => version.status === "Draft");
  const published = versions.data?.find((version) => version.status === "Published");
  const selectedVersionId = params.get("version") ?? draft?.id ?? published?.id ?? versions.data?.[0]?.id ?? "";
  const selectedVersion = versions.data?.find((version) => version.id === selectedVersionId);
  const rules = useQuery({
    queryKey: ["access-policy-rules", applicationId, selectedVersionId],
    enabled: Boolean(selectedVersionId),
    queryFn: ({ signal }) => apiRequest<AccessPolicyRule[]>(`/api/access-policies/applications/${applicationId}?policyVersionId=${selectedVersionId}`, { signal })
  });
  const publishedRules = useQuery({
    queryKey: ["access-policy-rules", applicationId, published?.id],
    enabled: Boolean(draft && published && selectedVersionId === draft.id),
    queryFn: ({ signal }) => apiRequest<AccessPolicyRule[]>(`/api/access-policies/applications/${applicationId}?policyVersionId=${published?.id}`, { signal })
  });
  const groups = useQuery({
    queryKey: ["groups", "policy-targets"],
    enabled: canReadGroups,
    queryFn: ({ signal }) => fetchAllAsPage<DirectoryGroupSummary>("/api/groups?isActive=true", signal)
  });

  const createDraft = useMutation({
    mutationFn: () => apiRequest<AccessPolicyVersion>(`/api/access-policies/applications/${applicationId}/drafts`, { method: "POST" }),
    onSuccess: async (created) => { await queryClient.invalidateQueries({ queryKey: ["access-policy-versions", applicationId] }); setParams({ version: created.id }); setFeedback(`Se creó el draft v${created.versionNumber}.`); }
  });
  const saveRule = useMutation({
    mutationFn: ({ values, rule }: { values: PolicyRuleFormValues; rule: AccessPolicyRule | undefined }) => apiRequest<AccessPolicyRule>(rule ? `/api/access-policies/${rule.id}` : "/api/access-policies", { method: rule ? "PUT" : "POST", body: JSON.stringify(rule ? { ...policyRulePayload(values, applicationId, selectedVersionId, false), version: rule.version } : policyRulePayload(values, applicationId, selectedVersionId, true)) }),
    onSuccess: async () => { setEditor(null); setFeedback("La regla del draft quedó guardada."); await Promise.all([queryClient.invalidateQueries({ queryKey: ["access-policy-rules", applicationId, selectedVersionId] }), queryClient.invalidateQueries({ queryKey: ["access-policy-versions", applicationId] })]); }
  });
  const removeRule = useMutation({
    mutationFn: (ruleId: string) => apiRequest<void>(`/api/access-policies/${ruleId}`, { method: "DELETE" }),
    onSuccess: async () => { setDeleteRule(null); setFeedback("La regla se eliminó del draft."); await Promise.all([queryClient.invalidateQueries({ queryKey: ["access-policy-rules", applicationId, selectedVersionId] }), queryClient.invalidateQueries({ queryKey: ["access-policy-versions", applicationId] })]); }
  });
  const simulate = useMutation({
    mutationFn: () => apiRequest<AccessPolicySimulation>("/api/access-policies/simulate", { method: "POST", body: JSON.stringify({ applicationSystemId: applicationId, policyVersionId: selectedVersionId || null, userId: simulationInput.userId, ipAddress: simulationInput.ipAddress || null, evaluatedAtUtc: simulationInput.evaluatedAtUtc ? new Date(`${simulationInput.evaluatedAtUtc}:00Z`).toISOString() : null, riskLevel: simulationInput.riskLevel, assuranceLevel: simulationInput.assuranceLevel }) })
  });
  const publish = useMutation({
    mutationFn: (proofToken: string) => apiRequest<AccessPolicyVersion>(`/api/access-policies/applications/${applicationId}/versions/${selectedVersionId}/publish`, { method: "POST", headers: { "X-AuthCenter-Reauthentication": proofToken } }),
    onSuccess: async (version) => { setPublishOpen(false); setFeedback(`La política v${version.versionNumber} quedó publicada y las sesiones de la aplicación fueron revocadas.`); await queryClient.invalidateQueries({ queryKey: ["access-policy-versions", applicationId] }); }
  });

  if (application.isPending || versions.isPending) return <PageState title="Cargando política" busy />;
  if (application.isError || versions.isError) return <PageState title="No pudimos cargar la política" detail={errorMessage(application.error ?? versions.error)} tone="error" action={<Link className="button" to="/access-policies">Volver</Link>} />;

  const isDraft = selectedVersion?.status === "Draft";
  const diff = isDraft ? diffPolicyRules(rules.data ?? [], publishedRules.data ?? []) : [];
  const title = application.data?.name ?? "Política de acceso";
  const operationError = createDraft.error ?? saveRule.error ?? removeRule.error ?? publish.error;

  function selectVersion(versionId: string): void { setParams({ version: versionId }); setEditor(null); simulate.reset(); }

  return <>
    <Breadcrumbs items={[{ label: "Políticas de acceso", to: "/access-policies" }, { label: title }]} />
    <PageHeader eyebrow={application.data?.code ?? "Seguridad"} title={title} description="Edita únicamente drafts, simula decisiones con contexto explícito y publica después de revisar el diff." actions={<Link className="button button--secondary" to="/access-policies">Volver al listado</Link>} />
    {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
    {operationError ? <p className="alert alert--error" role="alert">{errorMessage(operationError)}</p> : null}
    <section className="settings-panel" aria-labelledby="policy-versions"><div className="settings-panel__heading"><div><h2 id="policy-versions">Historial de versiones</h2><p>Las versiones publicadas y archivadas son inmutables.</p></div>{canWrite && !draft ? <button className="button" type="button" disabled={createDraft.isPending} onClick={() => createDraft.mutate()}>Crear draft</button> : null}</div>
      {versions.data?.length ? <div className="version-strip" role="list" aria-label="Versiones de política">{versions.data.map((version) => <div key={version.id} role="listitem"><button type="button" className={`version-card ${version.id === selectedVersionId ? "version-card--selected" : ""}`} onClick={() => selectVersion(version.id)}><strong>v{version.versionNumber}</strong><span>{version.status}</span><small>{version.ruleCount} reglas · {formatDate(version.publishedAt ?? version.createdAt)}</small></button></div>)}</div> : <PageState title="Sin política versionada" detail="El comportamiento actual permite acceso por defecto. Crea un draft para empezar a definir reglas." />}
    </section>

    {selectedVersion ? <section className="settings-panel" aria-labelledby="policy-rules"><div className="settings-panel__heading"><div><h2 id="policy-rules">Reglas de v{selectedVersion.versionNumber}</h2><p>Se evalúan en prioridad ascendente y decide la primera coincidencia.</p></div><div className="button-group">{isDraft && canWrite ? <button className="button" type="button" onClick={() => { saveRule.reset(); setEditor("new"); }}>Nueva regla</button> : null}</div></div>
      {rules.isPending ? <PageState title="Cargando reglas" busy /> : null}
      {rules.data?.length ? <div className="data-table" tabIndex={0} role="region" aria-label="Reglas ordenadas, desplazamiento horizontal"><table><caption className="sr-only">Reglas de acceso ordenadas</caption><thead><tr><th>Prioridad</th><th>Regla</th><th>Objetivo</th><th>Decisión</th><th>Estado</th><th><span className="sr-only">Acciones</span></th></tr></thead><tbody>{rules.data.map((rule) => <tr key={rule.id}><td>{rule.priority}</td><td><strong>{rule.name}</strong><span className="cell-detail">Assurance: {rule.requiredAssuranceLevel}</span></td><td>{rule.userEmail ?? rule.directoryGroupName ?? "Todos"}</td><td>{rule.action}{rule.mfaRequirement === "Required" ? " + MFA" : ""}</td><td><StatusBadge active={rule.isActive} /></td><td className="table-action">{isDraft && canWrite ? <div className="button-group"><button className="button button--small button--secondary" type="button" onClick={() => { saveRule.reset(); setEditor(rule); }}>Editar</button><button className="button button--small button--danger-quiet" type="button" onClick={() => setDeleteRule(rule)}>Eliminar</button></div> : "Inmutable"}</td></tr>)}</tbody></table></div> : rules.data ? <PageState title="Esta versión no tiene reglas" detail="Sin reglas activas, la versión permite acceso por defecto." /> : null}
    </section> : null}

    {editor && selectedVersion ? <PolicyRuleForm rule={editor === "new" ? undefined : editor} busy={saveRule.isPending} error={saveRule.error ? errorMessage(saveRule.error) : ""} applicationSystemId={applicationId ?? ""} canSearchUsers={canReadUsers} groups={groups.data?.items ?? []} onCancel={() => setEditor(null)} onSave={async (values) => { await saveRule.mutateAsync({ values, rule: editor === "new" ? undefined : editor }); }} /> : null}

    {selectedVersion ? <section className="settings-panel" aria-labelledby="policy-simulation"><div className="settings-panel__heading"><div><h2 id="policy-simulation">Simulación explicable</h2><p>Evalúa esta versión sin modificar el runtime ni revocar sesiones.</p></div></div><div className="form-grid">{canReadUsers ? <UserPicker label="Usuario" value={simulationInput.userId} selectedLabel={simulationUserLabel} applicationSystemId={applicationId} onChange={(userId, label) => { setSimulationInput((current) => ({ ...current, userId })); setSimulationUserLabel(label); }} /> : <label className="field"><span>Usuario (UUID)</span><input value={simulationInput.userId} onChange={(event) => setSimulationInput((current) => ({ ...current, userId: event.target.value }))} placeholder="User UUID" /></label>}<label className="field"><span>IP</span><input value={simulationInput.ipAddress} onChange={(event) => setSimulationInput((current) => ({ ...current, ipAddress: event.target.value }))} placeholder="203.0.113.10" /></label><label className="field"><span>Fecha y hora UTC</span><input type="datetime-local" value={simulationInput.evaluatedAtUtc} onChange={(event) => setSimulationInput((current) => ({ ...current, evaluatedAtUtc: event.target.value }))} /></label><label className="field"><span>Riesgo</span><select value={simulationInput.riskLevel} onChange={(event) => setSimulationInput((current) => ({ ...current, riskLevel: event.target.value }))}>{["Unknown", "Low", "Medium", "High", "Critical"].map((risk) => <option key={risk}>{risk}</option>)}</select></label><label className="field"><span>Assurance actual</span><select value={simulationInput.assuranceLevel} onChange={(event) => setSimulationInput((current) => ({ ...current, assuranceLevel: event.target.value }))}><option>Password</option><option value="Mfa">MFA</option><option>PhishingResistant</option></select></label></div><div className="form-footer"><button className="button" type="button" disabled={!simulationInput.userId || simulate.isPending} onClick={() => simulate.mutate()}>{simulate.isPending ? "Simulando…" : "Simular decisión"}</button></div>{simulate.error ? <p className="alert alert--error" role="alert">{errorMessage(simulate.error)}</p> : null}{simulate.data ? <div className={`decision-panel ${simulate.data.isAllowed ? "decision-panel--allow" : "decision-panel--deny"}`} role="status"><h3>{simulate.data.isAllowed ? "Acceso permitido" : "Acceso denegado"}{simulate.data.requireMfa ? " · requiere MFA" : ""}</h3><p>{simulate.data.decisionReason}</p><p>Primera coincidencia: <strong>{simulate.data.matchedRuleName ?? "ninguna"}</strong></p><ol>{simulate.data.ruleEvaluations.map((evaluation) => <li key={evaluation.ruleId}><strong>{evaluation.priority}. {evaluation.ruleName}</strong> — {evaluation.matched ? "coincide" : evaluation.reasons.join("; ")}</li>)}</ol></div> : null}</section> : null}

    {isDraft && selectedVersion ? <section className="settings-panel settings-panel--actions" aria-labelledby="policy-publish"><div className="settings-panel__heading"><div><h2 id="policy-publish">Diff y publicación</h2><p>Publicar archiva la versión anterior y revoca las sesiones activas de la aplicación.</p></div></div>{published ? diff.length ? <ul className="diff-list">{diff.map((entry) => <li key={`${entry.priority}-${entry.kind}`}><span className={`tag tag--${entry.kind}`}>{entry.kind === "added" ? "Agregada" : entry.kind === "removed" ? "Eliminada" : "Modificada"}</span><strong>{entry.priority}. {entry.name}</strong></li>)}</ul> : <p className="alert alert--info">El draft no tiene diferencias efectivas respecto a la versión publicada.</p> : <p className="alert alert--info">Esta será la primera versión publicada.</p>}<div className="form-footer"><button className="button" type="button" disabled={!canWrite || publish.isPending} onClick={() => setPublishOpen(true)}>Revisar y publicar v{selectedVersion.versionNumber}</button></div></section> : null}
    <ConfirmDialog open={Boolean(deleteRule)} title={`Eliminar ${deleteRule?.name ?? "regla"}`} detail="La regla se eliminará únicamente del draft actual." confirmLabel="Eliminar regla" dangerous busy={removeRule.isPending} onCancel={() => setDeleteRule(null)} onConfirm={() => { if (deleteRule) removeRule.mutate(deleteRule.id); }} />
    <ReauthenticationDialog open={publishOpen} purpose="admin.access-policy.publish" title={`Publicar política v${selectedVersion?.versionNumber ?? ""}`} detail="La nueva versión será efectiva inmediatamente y todas las sesiones activas de esta aplicación serán revocadas." confirmLabel="Verificar y publicar" dangerous onCancel={() => setPublishOpen(false)} onProof={async (proof) => { await publish.mutateAsync(proof); }} />
  </>;
}

