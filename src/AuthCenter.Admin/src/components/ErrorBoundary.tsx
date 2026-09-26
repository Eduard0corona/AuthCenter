import { Component, type ErrorInfo, type PropsWithChildren } from "react";
import { isStaleBuildError } from "../api/errors";
import { PageState } from "./PageState";

interface ErrorBoundaryProps extends PropsWithChildren {
  /** Clears the error when it changes (for example the route), so the operator can move on. */
  resetKey?: string;
}

interface ErrorBoundaryState {
  error: Error | null;
  reference: string | null;
}

/** Keeps a rendering failure inside the page that produced it, with a reference for support. */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null, reference: null };

  static getDerivedStateFromError(error: unknown): ErrorBoundaryState {
    return { error: error instanceof Error ? error : new Error(String(error)), reference: newReference() };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error(`[AuthCenter console ${this.state.reference ?? ""}]`, error, info.componentStack);
  }

  componentDidUpdate(previous: ErrorBoundaryProps): void {
    if (this.state.error && previous.resetKey !== this.props.resetKey) this.setState({ error: null, reference: null });
  }

  render() {
    const { error, reference } = this.state;
    if (!error) return this.props.children;
    const reload = <button className="button" type="button" onClick={() => window.location.reload()}>Recargar la página</button>;
    if (isStaleBuildError(error))
      return <PageState tone="error" title="Hay una versión nueva de la consola" detail="AuthCenter se actualizó mientras esta pestaña estaba abierta. Recarga para cargar la versión actual." action={reload} />;
    return <PageState tone="error" title="Esta sección dejó de funcionar" detail={`Recarga la página o vuelve a intentarlo más tarde. Si el problema continúa, comparte la referencia ${reference} con soporte.`} action={reload} />;
  }
}

function newReference(): string {
  return `UI-${Date.now().toString(36).toUpperCase()}`;
}
