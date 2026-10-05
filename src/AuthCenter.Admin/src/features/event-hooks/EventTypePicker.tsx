import { useMemo, useState } from "react";
import type { EventTypeInfo } from "../../api/types";
import { filterEventTypeGroups, groupEventTypes, WILDCARD } from "./event-hook";

interface EventTypePickerProps {
  catalog: readonly EventTypeInfo[];
  value: string[];
  onChange: (value: string[]) => void;
  disabled?: boolean | undefined;
  error?: string | undefined;
}

/** Event types by area, with a filter, per-area selection and the "every event" wildcard. */
export function EventTypePicker({ catalog, value, onChange, disabled = false, error }: EventTypePickerProps) {
  const [filter, setFilter] = useState("");
  const groups = useMemo(() => groupEventTypes(catalog), [catalog]);
  const visible = useMemo(() => filterEventTypeGroups(groups, filter), [filter, groups]);
  const selected = new Set(value);
  const everything = selected.has(WILDCARD);
  const known = new Set(catalog.map((item) => item.type));
  const retired = value.filter((type) => type !== WILDCARD && !known.has(type));

  function toggle(types: string[], checked: boolean): void {
    const next = new Set(value);
    next.delete(WILDCARD);
    for (const type of types) {
      if (checked) next.add(type); else next.delete(type);
    }
    onChange([...next]);
  }

  return (
    <fieldset className="check-group event-type-picker" disabled={disabled} aria-describedby={error ? "event-types-error" : undefined}>
      <legend>Tipos de evento</legend>
      <label className="checkbox-field event-type-picker__all">
        <input type="checkbox" checked={everything} onChange={(event) => onChange(event.target.checked ? [WILDCARD] : [])} />
        <span>Todos los eventos, incluidos los que se agreguen en versiones futuras (<code>*</code>)</span>
      </label>
      {!everything ? <>
        <div className="event-type-picker__toolbar">
          <label className="field"><span>Filtrar tipos</span><input type="search" value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="LOGIN, MFA, USER…" /></label>
          <p className="field-help" aria-live="polite">{selected.size === 1 ? "1 tipo seleccionado" : `${selected.size} tipos seleccionados`} (máximo 100)</p>
        </div>
        {retired.length > 0 ? <div className="alert alert--warning"><p>Este webhook incluye tipos que ya no existen: {retired.join(", ")}. Quítalos para poder guardar.</p><button className="button button--small button--secondary" type="button" onClick={() => onChange(value.filter((type) => known.has(type)))}>Quitar tipos retirados</button></div> : null}
        <div className="event-type-groups">
          {visible.map((group) => {
            const all = group.types.every((type) => selected.has(type));
            const some = !all && group.types.some((type) => selected.has(type));
            return (
              <section className="event-type-group" key={group.category} aria-labelledby={`event-group-${group.category}`}>
                <label className="event-type-group__heading">
                  <input type="checkbox" checked={all} ref={(node) => { if (node) node.indeterminate = some; }} onChange={(event) => toggle(group.types, event.target.checked)} aria-label={`Todos los eventos de ${group.label}`} />
                  <strong id={`event-group-${group.category}`}>{group.label}</strong>
                  <span className="muted">{group.types.filter((type) => selected.has(type)).length}/{group.types.length}</span>
                </label>
                <div className="event-type-group__types">
                  {group.types.map((type) => <label className="event-type-option" key={type}><input type="checkbox" checked={selected.has(type)} onChange={(event) => toggle([type], event.target.checked)} /><code>{type}</code></label>)}
                </div>
              </section>
            );
          })}
          {visible.length === 0 ? <p className="muted">Ningún tipo coincide con el filtro.</p> : null}
        </div>
      </> : null}
      {error ? <p className="field-error" id="event-types-error">{error}</p> : null}
    </fieldset>
  );
}
