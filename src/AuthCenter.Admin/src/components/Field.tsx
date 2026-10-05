import { cloneElement, isValidElement, useId, type ReactElement, type ReactNode } from "react";

interface DescribedControl {
  "aria-describedby"?: string | undefined;
  "aria-invalid"?: boolean | undefined;
}

interface FieldProps {
  label: ReactNode;
  error?: string | undefined;
  help?: ReactNode;
  children: ReactNode;
}

/**
 * A labelled control with its help text and validation error. Help and error stay outside the
 * label, so the control's name is only its label; they describe it (aria-describedby) instead.
 */
export function Field({ label, error, help, children }: FieldProps) {
  const helpId = useId();
  const errorId = useId();
  const describedBy = [help ? helpId : null, error ? errorId : null].filter(Boolean).join(" ") || undefined;
  const control = isValidElement(children)
    ? cloneElement(children as ReactElement<DescribedControl>, { "aria-describedby": describedBy, "aria-invalid": error ? true : undefined })
    : children;
  return (
    <div className="field">
      <label className="field__label"><span>{label}</span>{control}</label>
      {help ? <span className="field-help" id={helpId}>{help}</span> : null}
      {error ? <span className="field-error" id={errorId}>{error}</span> : null}
    </div>
  );
}
