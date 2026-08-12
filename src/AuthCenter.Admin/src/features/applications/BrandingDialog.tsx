import { zodResolver } from "@hookform/resolvers/zod";
import { useEffect, useRef } from "react";
import { useForm, useWatch } from "react-hook-form";
import type { ApplicationSummary } from "../../api/types";
import { brandingSchema, contrastRatio, type BrandingFormValues } from "./branding";

interface BrandingDialogProps {
  application: ApplicationSummary | null;
  busy: boolean;
  error: string;
  onSave: (values: BrandingFormValues) => Promise<void>;
  onClose: () => void;
}

export function BrandingDialog({ application, busy, error, onSave, onClose }: BrandingDialogProps) {
  const dialogRef = useRef<HTMLDialogElement>(null);
  const { register, handleSubmit, reset, control, formState: { errors } } = useForm<BrandingFormValues>({ resolver: zodResolver(brandingSchema) });
  const values = useWatch({ control });

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    if (application) {
      const branding = application.branding;
      reset({
        displayName: branding?.displayName ?? application.name,
        primaryColor: branding?.primaryColor.toLowerCase() ?? "#2563eb",
        backgroundColor: branding?.backgroundColor.toLowerCase() ?? "#f8fafc",
        logoUrl: branding?.logoUrl ?? "",
        supportUrl: branding?.supportUrl ?? "",
        privacyUrl: branding?.privacyUrl ?? "",
        termsUrl: branding?.termsUrl ?? ""
      });
      if (!dialog.open) dialog.showModal();
    } else if (dialog.open) dialog.close();
  }, [application, reset]);

  const ratio = values.primaryColor && values.backgroundColor ? contrastRatio(values.primaryColor, values.backgroundColor) : 0;
  return (
    <dialog ref={dialogRef} className="dialog dialog--wide" onCancel={(event) => { event.preventDefault(); if (!busy) onClose(); }}>
      <form onSubmit={(event) => void handleSubmit(onSave)(event)}>
        <div className="dialog__heading"><div><p className="eyebrow">Aplicaciones</p><h2>Editar branding</h2><p>{application?.code}</p></div><button className="icon-button" type="button" onClick={onClose} disabled={busy} aria-label="Cerrar">×</button></div>
        <div className="branding-layout">
          <div className="form-stack">
            <Field label="Nombre visible" error={errors.displayName?.message}><input {...register("displayName")} maxLength={100} /></Field>
            <div className="form-grid">
              <Field label="Color principal" error={errors.primaryColor?.message}><input {...register("primaryColor")} type="color" /></Field>
              <Field label="Fondo" error={errors.backgroundColor?.message}><input {...register("backgroundColor")} type="color" /></Field>
            </div>
            <p className={`contrast-result ${ratio >= 4.5 ? "contrast-result--pass" : "contrast-result--fail"}`}>Contraste {ratio.toFixed(2)}:1 · {ratio >= 4.5 ? "Cumple AA" : "No cumple AA"}</p>
            <Field label="Logo HTTPS" error={errors.logoUrl?.message}><input {...register("logoUrl")} type="url" placeholder="https://…" /></Field>
            <Field label="Soporte HTTPS" error={errors.supportUrl?.message}><input {...register("supportUrl")} type="url" placeholder="https://…" /></Field>
            <Field label="Privacidad HTTPS" error={errors.privacyUrl?.message}><input {...register("privacyUrl")} type="url" placeholder="https://…" /></Field>
            <Field label="Términos HTTPS" error={errors.termsUrl?.message}><input {...register("termsUrl")} type="url" placeholder="https://…" /></Field>
          </div>
          <section className="brand-preview" style={{ backgroundColor: values.backgroundColor }} aria-label="Vista previa del branding">
            <p>Vista previa</p>
            <div className="brand-preview__card">
              {values.logoUrl ? <img src={values.logoUrl} alt="" /> : <span className="brand-preview__placeholder" aria-hidden="true">A</span>}
              <h3>{values.displayName || "Nombre de aplicación"}</h3>
              <button type="button" style={{ backgroundColor: values.primaryColor }}>Continuar</button>
              <div>{values.privacyUrl ? <a href={values.privacyUrl} target="_blank" rel="noreferrer noopener">Privacidad</a> : null}{values.termsUrl ? <a href={values.termsUrl} target="_blank" rel="noreferrer noopener">Términos</a> : null}</div>
            </div>
          </section>
        </div>
        {error ? <p className="alert alert--error" role="alert">{error}</p> : null}
        <div className="dialog__actions"><button className="button button--secondary" type="button" onClick={onClose} disabled={busy}>Cancelar</button><button className="button" type="submit" disabled={busy}>{busy ? "Guardando…" : "Guardar branding"}</button></div>
      </form>
    </dialog>
  );
}

function Field({ label, error, children }: { label: string; error: string | undefined; children: React.ReactNode }) {
  return <label className="field"><span>{label}</span>{children}{error ? <span className="field-error">{error}</span> : null}</label>;
}
