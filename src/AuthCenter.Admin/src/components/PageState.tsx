import { useDocumentTitle } from "../hooks/useDocumentTitle";

interface PageStateProps {
  title: string;
  detail?: string;
  tone?: "neutral" | "error" | "forbidden";
  busy?: boolean;
  action?: React.ReactNode;
  /** Set when the state is the whole page (not found, forbidden): it names the browser tab. */
  documentTitle?: string;
}

export function PageState({ title, detail, tone = "neutral", busy = false, action, documentTitle }: PageStateProps) {
  useDocumentTitle(documentTitle);
  return (
    <section className={`page-state page-state--${tone}`} aria-live="polite" aria-busy={busy || undefined}>
      {busy ? <span className="spinner" aria-hidden="true" /> : null}
      <h1>{title}</h1>
      {detail ? <p>{detail}</p> : null}
      {action}
    </section>
  );
}
