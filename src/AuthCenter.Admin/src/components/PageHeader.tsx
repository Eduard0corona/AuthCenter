import { useEffect, useRef, type PropsWithChildren, type ReactNode } from "react";
import { useDocumentTitle } from "../hooks/useDocumentTitle";

interface PageHeaderProps extends PropsWithChildren {
  eyebrow?: string;
  title: string;
  /** The browser tab's name when the heading is not a good one (a greeting, for example). */
  documentTitle?: string;
  description?: string;
  actions?: ReactNode;
}

export function PageHeader({ eyebrow, title, documentTitle, description, actions, children }: PageHeaderProps) {
  const headingRef = useRef<HTMLHeadingElement>(null);
  // Focus starts on the heading so screen readers announce the new page.
  useEffect(() => headingRef.current?.focus(), []);
  useDocumentTitle(documentTitle ?? title);

  return (
    <header className="page-header">
      <div>
        {eyebrow ? <p className="eyebrow">{eyebrow}</p> : null}
        <h1 ref={headingRef} tabIndex={-1}>{title}</h1>
        {description ? <p className="page-description">{description}</p> : null}
      </div>
      {actions ? <div className="page-actions">{actions}</div> : null}
      {children}
    </header>
  );
}
