import { useQuery } from "@tanstack/react-query";
import { useId, useState, type KeyboardEvent } from "react";
import { apiRequest } from "../api/client";
import { errorMessage } from "../api/errors";
import type { PagedResult, UserSummary } from "../api/types";
import { useDebouncedValue } from "../hooks/useDebouncedValue";
import { buildQuery } from "../utils/format";

interface UserPickerProps {
  label: string;
  /** The selected user's id ("" when none). */
  value: string;
  /** How the selected user is shown (name and email) until the operator searches again. */
  selectedLabel: string;
  onChange: (userId: string, label: string) => void;
  /** Only users with access to this application. */
  applicationSystemId?: string | undefined;
  error?: string | undefined;
  disabled?: boolean | undefined;
}

const RESULTS = 10;

/**
 * Finds a user by name or email as the operator types (the directory can be far larger than one
 * page), following the ARIA combobox pattern.
 */
export function UserPicker({ label, value, selectedLabel, onChange, applicationSystemId, error, disabled = false }: UserPickerProps) {
  const inputId = useId();
  const listId = useId();
  const errorId = useId();
  const [query, setQuery] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const text = query ?? selectedLabel;
  const search = useDebouncedValue((query ?? "").trim(), 250);
  const results = useQuery({
    queryKey: ["users", "picker", search, applicationSystemId ?? ""],
    enabled: open && search.length >= 2,
    queryFn: ({ signal }) => apiRequest<PagedResult<UserSummary>>(`/api/users?${buildQuery({ page: 1, pageSize: RESULTS, search, isActive: true, applicationSystemId })}`, { signal })
  });
  const options = search.length >= 2 ? results.data?.items ?? [] : [];

  function choose(user: UserSummary): void {
    onChange(user.id, `${user.fullName} · ${user.email}`);
    setQuery(null);
    setOpen(false);
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>): void {
    if (event.key === "ArrowDown") { event.preventDefault(); setOpen(true); setActive((index) => Math.min(index + 1, Math.max(options.length - 1, 0))); }
    else if (event.key === "ArrowUp") { event.preventDefault(); setActive((index) => Math.max(index - 1, 0)); }
    else if (event.key === "Enter" && open && options[active]) { event.preventDefault(); choose(options[active]); }
    else if (event.key === "Escape" && open) { event.preventDefault(); setOpen(false); setQuery(null); }
  }

  const status = search.length < 2 ? "Escribe al menos 2 letras del nombre o correo." : results.isFetching ? "Buscando…" : results.isError ? errorMessage(results.error) : options.length === 0 ? "Ningún usuario coincide." : `${options.length === RESULTS ? `Primeros ${RESULTS}` : options.length} resultados.`;

  return (
    <div className="field user-picker">
      <label htmlFor={inputId}>{label}</label>
      <div className="user-picker__control">
        <input
          id={inputId}
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={open && options[active] ? `${listId}-${options[active].id}` : undefined}
          aria-describedby={error ? errorId : undefined}
          aria-invalid={error ? true : undefined}
          value={text}
          placeholder="Buscar por nombre o correo"
          autoComplete="off"
          disabled={disabled}
          onChange={(event) => { setQuery(event.target.value); setOpen(true); setActive(0); }}
          onFocus={() => { if (query !== null) setOpen(true); }}
          onBlur={() => { setOpen(false); }}
          onKeyDown={onKeyDown}
        />
        {value && !disabled ? <button className="button button--small button--secondary" type="button" onClick={() => { onChange("", ""); setQuery(""); }}>Quitar<span className="sr-only"> {selectedLabel}</span></button> : null}
        <ul id={listId} role="listbox" aria-label={`Usuarios para ${label}`} className="user-picker__options" hidden={!open || options.length === 0}>
        {options.map((user, index) => (
          <li
            key={user.id}
            id={`${listId}-${user.id}`}
            role="option"
            aria-selected={index === active}
            className={index === active ? "user-picker__option user-picker__option--active" : "user-picker__option"}
            // mousedown fires before the input's blur closes the list.
            onMouseDown={(event) => { event.preventDefault(); choose(user); }}
          >
            <strong>{user.fullName}</strong><span>{user.email}</span>
          </li>
        ))}
        </ul>
      </div>
      {open ? <span className="field-help" role="status">{status}</span> : null}
      {error ? <span className="field-error" id={errorId}>{error}</span> : null}
    </div>
  );
}
