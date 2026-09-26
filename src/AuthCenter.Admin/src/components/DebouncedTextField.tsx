import { useEffect, useState } from "react";
import { useDebouncedValue } from "../hooks/useDebouncedValue";

interface DebouncedTextFieldProps {
  label: string;
  /** The committed value (usually a URL parameter). */
  value: string;
  onCommit: (value: string) => void;
  placeholder?: string;
  list?: string;
  normalize?: (value: string) => string;
  help?: string;
}

/** A filter that commits what the operator types once they pause, and follows outside changes. */
export function DebouncedTextField({ label, value, onCommit, placeholder, list, normalize = (text) => text.trim(), help }: DebouncedTextFieldProps) {
  const [draft, setDraft] = useState(value);
  const [committed, setCommitted] = useState(value);
  if (value !== committed) {
    // The filter changed elsewhere (a link, "clear filters"): show it.
    setCommitted(value);
    setDraft(value);
  }
  const debounced = useDebouncedValue(draft);
  useEffect(() => {
    const next = normalize(debounced);
    if (debounced === draft && next !== value) onCommit(next);
  }, [debounced, draft, normalize, onCommit, value]);

  return (
    <label className="field">
      <span>{label}</span>
      <input value={draft} onChange={(event) => setDraft(event.target.value)} placeholder={placeholder} list={list} autoComplete="off" spellCheck={false} />
      {help ? <small className="field-help">{help}</small> : null}
    </label>
  );
}
