import type { PropsWithChildren } from "react";
import { permissionLabel } from "../auth/permissions";
import { useSession } from "../auth/session";
import { PageState } from "../components/PageState";

export function PermissionRoute({ permission, children }: PropsWithChildren<{ permission: string }>) {
  const { permissions } = useSession();
  if (!permissions.has(permission)) {
    return (
      <PageState
        tone="forbidden"
        title="Acceso restringido"
        documentTitle="Acceso restringido"
        detail={`Para abrir esta sección necesitas el permiso «${permissionLabel(permission)}». Pídeselo a un administrador de AuthCenter.`}
        action={<p className="muted">Código del permiso: <span className="mono">{permission}</span></p>}
      />
    );
  }
  return children;
}
