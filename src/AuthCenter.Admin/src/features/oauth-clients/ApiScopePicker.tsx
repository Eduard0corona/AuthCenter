import { useQuery } from "@tanstack/react-query";
import { useId } from "react";
import { Link } from "react-router-dom";
import { fetchAllPages } from "../../api/catalog";
import { errorMessage } from "../../api/errors";
import type { ApiResource } from "../../api/types";
import { splitApiScopes } from "./oauth-client";

interface ApiScopePickerProps {
  /** The selected API scopes, one per line (the form's value). */
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean | undefined;
  error?: string | undefined;
}

/** API scopes from the catalog, by API; the access token's audience is the API of the scopes requested. */
export function ApiScopePicker({ value, onChange, disabled = false, error }: ApiScopePickerProps) {
  const errorId = useId();
  const catalog = useQuery({
    queryKey: ["api-resources", "catalog"],
    queryFn: ({ signal }) => fetchAllPages<ApiResource>("/api/api-resources", signal)
  });
  const selected = splitApiScopes(value);
  const known = new Set(catalog.data?.flatMap((resource) => resource.scopes.map((scope) => scope.name)) ?? []);
  const unknown = catalog.data ? selected.filter((scope) => !known.has(scope)) : [];

  function toggle(scope: string, checked: boolean): void {
    const next = checked ? [...selected, scope] : selected.filter((item) => item !== scope);
    onChange([...new Set(next)].join("\n"));
  }

  return (
    <fieldset className="check-group" disabled={disabled} aria-describedby={error ? errorId : undefined}>
      <legend>Scopes de APIs</legend>
      <p className="field-help">El access token tendrá como audiencia el API de los scopes pedidos. Administra el catálogo en <Link to="/api-resources">Recursos de API</Link>.</p>
      {catalog.isPending ? <p className="muted">Cargando el catálogo de APIs…</p> : null}
      {catalog.isError ? <p className="alert alert--error" role="alert">{errorMessage(catalog.error)}</p> : null}
      {catalog.data && catalog.data.length === 0 ? <p className="muted">Todavía no hay APIs registrados.</p> : null}
      {catalog.data?.map((resource) => (
        <div className="api-scope-group" key={resource.id} role="group" aria-labelledby={`api-${resource.id}`}>
          <p id={`api-${resource.id}`} className="api-scope-group__title"><strong>{resource.displayName}</strong> <span className="mono muted">{resource.identifier}</span>{resource.isActive ? null : <span className="tag tag--warning">Inactivo</span>}</p>
          <div className="checkbox-grid">
            {resource.scopes.map((scope) => <label className="checkbox-field" key={scope.id} title={scope.description ?? undefined}><input type="checkbox" checked={selected.includes(scope.name)} onChange={(event) => toggle(scope.name, event.target.checked)} /><span><code>{scope.name}</code> · {scope.displayName}</span></label>)}
          </div>
        </div>
      ))}
      {unknown.length > 0 ? <div className="alert alert--warning"><p>El cliente tiene scopes que ya no existen en el catálogo: {unknown.join(", ")}. Guardar fallará mientras los conserve.</p><button className="button button--small button--secondary" type="button" onClick={() => onChange(selected.filter((scope) => known.has(scope)).join("\n"))}>Quitar scopes retirados</button></div> : null}
      {error ? <p className="field-error" id={errorId}>{error}</p> : null}
    </fieldset>
  );
}
