/**
 * AuthCenter's own permissions by what they let an operator do. The console names a permission by
 * its readable name; the code (AUTHCENTER_…, reserved to AuthCenter's application) is a secondary
 * detail for whoever administers roles.
 */
export const PERMISSION_LABELS: Readonly<Record<string, string>> = {
  AUTHCENTER_USERS_READ: "Consultar usuarios",
  AUTHCENTER_USERS_WRITE: "Editar usuarios",
  AUTHCENTER_APPLICATIONS_READ: "Consultar aplicaciones",
  AUTHCENTER_APPLICATIONS_WRITE: "Editar aplicaciones",
  AUTHCENTER_ROLES_READ: "Consultar roles",
  AUTHCENTER_ROLES_WRITE: "Editar roles",
  AUTHCENTER_PERMISSIONS_READ: "Consultar permisos",
  AUTHCENTER_PERMISSIONS_WRITE: "Editar permisos",
  AUTHCENTER_AUDIT_LOGS_READ: "Consultar el registro de actividad",
  AUTHCENTER_OAUTH_CLIENTS_READ: "Consultar clientes OAuth y APIs",
  AUTHCENTER_OAUTH_CLIENTS_WRITE: "Editar clientes OAuth y APIs",
  AUTHCENTER_GROUPS_READ: "Consultar grupos",
  AUTHCENTER_GROUPS_WRITE: "Editar grupos",
  AUTHCENTER_ACCESS_POLICIES_READ: "Consultar políticas de acceso",
  AUTHCENTER_ACCESS_POLICIES_WRITE: "Editar políticas de acceso",
  AUTHCENTER_PROFILE_SCHEMAS_READ: "Consultar el esquema de perfil",
  AUTHCENTER_PROFILE_SCHEMAS_WRITE: "Editar el esquema de perfil",
  AUTHCENTER_EVENT_HOOKS_READ: "Consultar webhooks de eventos",
  AUTHCENTER_EVENT_HOOKS_WRITE: "Editar webhooks de eventos",
  AUTHCENTER_FEDERATION_READ: "Consultar la federación",
  AUTHCENTER_FEDERATION_WRITE: "Editar la federación",
  AUTHCENTER_PROVISIONING_READ: "Consultar el aprovisionamiento (SCIM)",
  AUTHCENTER_PROVISIONING_WRITE: "Editar el aprovisionamiento (SCIM)",
  AUTHCENTER_SAML_APPS_READ: "Consultar aplicaciones SAML",
  AUTHCENTER_SAML_APPS_WRITE: "Editar aplicaciones SAML",
  AUTHCENTER_GOVERNANCE_READ: "Consultar gobierno de accesos",
  AUTHCENTER_GOVERNANCE_WRITE: "Editar gobierno de accesos"
};

/** A permission's readable name: AuthCenter's own by its code, any other by the name it was given. */
export function permissionLabel(code: string, name?: string | null): string {
  return PERMISSION_LABELS[code] ?? (name?.trim() || code);
}

/** "Necesitas el permiso «…» para …": what to ask an administrator for. */
export function needPermission(code: string, purpose: string): string {
  return `Necesitas el permiso «${permissionLabel(code)}» para ${purpose}.`;
}

/** "Para …, pide a un administrador el permiso «…».": the way out of a read-only view. */
export function askForPermission(code: string, purpose: string): string {
  return `Para ${purpose}, pide a un administrador el permiso «${permissionLabel(code)}».`;
}
