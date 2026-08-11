import { useMutation, useQuery } from "@tanstack/react-query";
import { createContext, type PropsWithChildren, useContext, useMemo } from "react";
import { apiRequest, setCsrfToken } from "../api/client";
import type { SessionResponse, SessionUser } from "../api/types";
import { PageState } from "../components/PageState";

interface SessionContextValue {
  user: SessionUser;
  permissions: ReadonlySet<string>;
  signOut: () => void;
  signingOut: boolean;
}

const SessionContext = createContext<SessionContextValue | null>(null);

export function SessionProvider({ children }: PropsWithChildren) {
  const session = useQuery({
    queryKey: ["session"],
    queryFn: async () => {
      const value = await apiRequest<SessionResponse>("/ui-api/session");
      setCsrfToken(value.csrfToken);
      return value;
    },
    staleTime: Number.POSITIVE_INFINITY,
    retry: false
  });
  const { mutate: mutateLogout, isPending: signingOut } = useMutation({
    mutationFn: () => apiRequest<void>("/ui-api/session/logout", { method: "POST" }),
    onSettled: () => window.location.replace("/login")
  });
  const value = useMemo<SessionContextValue | null>(() => {
    if (!session.data) return null;
    return {
      user: session.data.user,
      permissions: new Set(session.data.user.permissions),
      signOut: () => mutateLogout(),
      signingOut
    };
  }, [mutateLogout, session.data, signingOut]);

  if (session.isPending) return <PageState title="Validando sesión" detail="Preparando la consola administrativa." busy />;
  if (session.isError || !value) return <PageState title="No pudimos abrir la consola" detail="Actualiza la página o inicia sesión nuevamente." tone="error" />;
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

// eslint-disable-next-line react-refresh/only-export-components
export function useSession(): SessionContextValue {
  const value = useContext(SessionContext);
  if (!value) throw new Error("useSession must be used inside SessionProvider.");
  return value;
}

export function PermissionGate({ permission, children }: PropsWithChildren<{ permission: string }>) {
  const { permissions } = useSession();
  return permissions.has(permission) ? children : null;
}
