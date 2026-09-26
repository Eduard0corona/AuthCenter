import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ApplicationGovernance, GovernanceUser } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageState } from "../../components/PageState";
import { SaveError } from "../../components/SaveError";
import { UserPicker } from "../../components/UserPicker";
import { governanceMessages } from "./governance";

interface Draft {
  owners: GovernanceUser[];
  accessRequestsEnabled: boolean;
  version: number;
}

/** Who answers for the application (approves its requests, reviews its access) and whether users may request it. */
export function ApplicationGovernancePanel({ applicationId }: { applicationId: string }) {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_GOVERNANCE_WRITE");
  const canPickUsers = permissions.has("AUTHCENTER_USERS_READ");
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<Draft | null>(null);
  const [candidate, setCandidate] = useState({ id: "", label: "" });
  const [feedback, setFeedback] = useState("");
  const governance = useQuery({
    queryKey: ["application-governance", applicationId],
    queryFn: ({ signal }) => apiRequest<ApplicationGovernance>(`/api/governance/applications/${applicationId}`, { signal })
  });
  const save = useMutation({
    mutationFn: (values: Draft) => apiRequest<ApplicationGovernance>(`/api/governance/applications/${applicationId}`, {
      method: "PUT",
      body: JSON.stringify({ accessRequestsEnabled: values.accessRequestsEnabled, ownerUserIds: values.owners.map((owner) => owner.id), version: values.version })
    }),
    onSuccess: (result) => {
      queryClient.setQueryData(["application-governance", applicationId], result);
      setDraft(null);
      setFeedback("Responsables y solicitudes guardados.");
    }
  });

  if (governance.isPending) return <PageState title="Cargando responsables" busy />;
  if (governance.isError) return <PageState title="No pudimos cargar los responsables" detail={errorMessage(governance.error)} tone="error" action={<button className="button" type="button" onClick={() => void governance.refetch()}>Reintentar</button>} />;
  const saved = governance.data;
  const current: Draft = draft ?? { owners: saved.owners, accessRequestsEnabled: saved.accessRequestsEnabled, version: saved.version };
  const edit = (next: Partial<Draft>) => { setFeedback(""); setDraft({ ...current, ...next }); };

  function addOwner(): void {
    if (!candidate.id || current.owners.some((owner) => owner.id === candidate.id)) return;
    const [fullName = candidate.label, email = ""] = candidate.label.split(" · ");
    edit({ owners: [...current.owners, { id: candidate.id, fullName, email, isActive: true }] });
    setCandidate({ id: "", label: "" });
  }

  return (
    <section className="settings-panel" aria-labelledby="application-governance">
      <div className="settings-panel__heading"><div><h2 id="application-governance">Responsables y solicitudes de acceso</h2><p>Los responsables aprueban las solicitudes de acceso y revisan quién conserva el acceso, desde su portal. Nunca deciden sobre sí mismos.</p></div></div>
      {feedback ? <p className="alert alert--success" role="status">{feedback}</p> : null}
      <SaveError error={save.error} messages={governanceMessages} onReload={() => { save.reset(); setDraft(null); void governance.refetch(); }} />
      <fieldset className="settings-fieldset" disabled={!canWrite || save.isPending}>
        <legend className="sr-only">Responsables</legend>
        {current.owners.length === 0 ? <p className="muted">Sin responsables: las solicitudes y revisiones las decide un administrador con AUTHCENTER_GOVERNANCE_WRITE.</p> : (
          <ul className="owner-list" aria-label="Responsables">
            {current.owners.map((owner) => <li key={owner.id}>
              <span><strong>{owner.fullName}</strong> {owner.email ? <span className="muted">{owner.email}</span> : null}{owner.isActive ? null : <span className="tag tag--inactive">Inactivo</span>}</span>
              {canWrite ? <button className="button button--small button--secondary" type="button" onClick={() => edit({ owners: current.owners.filter((item) => item.id !== owner.id) })}>Quitar<span className="sr-only"> a {owner.fullName}</span></button> : null}
            </li>)}
          </ul>
        )}
        {canWrite && canPickUsers ? <div className="form-grid">
          <UserPicker label="Agregar responsable" value={candidate.id} selectedLabel={candidate.label} onChange={(id, label) => setCandidate({ id, label })} />
          <div className="field"><span aria-hidden="true">&nbsp;</span><button className="button button--secondary" type="button" disabled={!candidate.id} onClick={addOwner}>Agregar</button></div>
        </div> : null}
        <div className="checkbox-grid"><label className="checkbox-field"><input type="checkbox" checked={current.accessRequestsEnabled} onChange={(event) => edit({ accessRequestsEnabled: event.target.checked })} /><span>Los usuarios pueden solicitar acceso desde su portal</span></label></div>
      </fieldset>
      {canWrite ? <div className="button-group">
        <button className="button" type="button" disabled={!draft || save.isPending} onClick={() => { if (draft) save.mutate(draft); }}>{save.isPending ? "Guardando…" : "Guardar responsables"}</button>
        {draft ? <button className="button button--secondary" type="button" onClick={() => setDraft(null)}>Descartar cambios</button> : null}
      </div> : null}
    </section>
  );
}
