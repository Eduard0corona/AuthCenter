import type { PropsWithChildren } from "react";
import { useSession } from "../auth/session";
import { PageState } from "../components/PageState";

export function PermissionRoute({ permission, children }: PropsWithChildren<{ permission: string }>) {
  const { permissions } = useSession();
  if (!permissions.has(permission)) {
    return <PageState tone="forbidden" title="Acceso restringido" documentTitle="Acceso restringido" detail="Tu cuenta no tiene el permiso necesario para abrir esta sección. Si necesitas acceso, contacta a un SuperAdmin." />;
  }
  return children;
}
