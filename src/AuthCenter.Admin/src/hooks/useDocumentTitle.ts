import { useEffect } from "react";

/** The console's name: the browser tab, the history and screen readers announce it. */
export const CONSOLE_TITLE = "Consola de administración";

/** "{page} · Consola de administración", or the console's name alone. */
export function documentTitle(page?: string | null): string {
  const name = page?.trim();
  return name ? `${name} · ${CONSOLE_TITLE}` : CONSOLE_TITLE;
}

/** Names the browser tab after the page (WCAG 2.4.2); undefined leaves the title as it is. */
export function useDocumentTitle(page: string | undefined): void {
  useEffect(() => {
    if (page !== undefined) document.title = documentTitle(page);
  }, [page]);
}
