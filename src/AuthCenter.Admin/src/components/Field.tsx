import type { ReactNode } from "react";

/** A labelled control with its help text and validation error. */
export function Field({ label, error, help, children }: { label: ReactNode; error?: string | undefined; help?: string | undefined; children: ReactNode }) {
  return <label className="field"><span>{label}</span>{children}{help ? <span className="field-help">{help}</span> : null}{error ? <span className="field-error">{error}</span> : null}</label>;
}
