/** Console pages for the entities the audit trail names, with the permission each one needs. */
interface EntityRoute {
  label: string;
  permission: string;
  path: (id: string) => string;
}

const ENTITY_ROUTES: Record<string, EntityRoute> = {
  ApplicationUser: { label: "Usuario", permission: "AUTHCENTER_USERS_READ", path: (id) => `/users/${id}` },
  ApplicationSystem: { label: "Aplicación", permission: "AUTHCENTER_APPLICATIONS_READ", path: (id) => `/applications/${id}` },
  ApplicationRole: { label: "Rol", permission: "AUTHCENTER_ROLES_READ", path: (id) => `/roles/${id}` },
  Permission: { label: "Permiso", permission: "AUTHCENTER_PERMISSIONS_READ", path: (id) => `/permissions/${id}` },
  DirectoryGroup: { label: "Grupo", permission: "AUTHCENTER_GROUPS_READ", path: (id) => `/groups/${id}` },
  OAuthClient: { label: "OAuth client", permission: "AUTHCENTER_OAUTH_CLIENTS_READ", path: (id) => `/oauth-clients/${encodeURIComponent(id)}` },
  ApiResource: { label: "Recurso de API", permission: "AUTHCENTER_OAUTH_CLIENTS_READ", path: (id) => `/api-resources/${id}` },
  FederationProvider: { label: "Proveedor federado", permission: "AUTHCENTER_FEDERATION_READ", path: (id) => `/federation/providers/${id}` },
  ProvisioningToken: { label: "Provisioning token", permission: "AUTHCENTER_PROVISIONING_READ", path: (id) => `/provisioning-tokens/${id}` },
  EventHook: { label: "Event hook", permission: "AUTHCENTER_EVENT_HOOKS_READ", path: (id) => `/event-hooks/${id}` },
  ProfileMapping: { label: "Profile mapping", permission: "AUTHCENTER_USERS_READ", path: (id) => `/profile-mappings/${id}` },
  DynamicGroupRule: { label: "Group rule", permission: "AUTHCENTER_GROUPS_READ", path: (id) => `/group-rules/${id}` },
  UserProfileAttributeDefinition: { label: "Atributo de perfil", permission: "AUTHCENTER_PROFILE_SCHEMAS_READ", path: (id) => `/profile-schema/${id}` }
};

export function entityLabel(entityName: string | null): string {
  if (!entityName) return "—";
  return ENTITY_ROUTES[entityName]?.label ?? entityName;
}

/** The page of the entity, when the console has one and the operator may open it. */
export function entityPath(entityName: string | null, entityId: string | null, permissions: ReadonlySet<string>): string | null {
  if (!entityName || !entityId) return null;
  const route = ENTITY_ROUTES[entityName];
  return route && permissions.has(route.permission) ? route.path(entityId) : null;
}

/** The System Log filtered to one entity. */
export function historyPath(entityName: string, entityId: string): string {
  return `/system-log?${new URLSearchParams({ entity: entityName, entityId }).toString()}`;
}

/** The event's metadata as indented JSON, or the raw text when it is not JSON. */
export function formatMetadata(metadataJson: string | null): string | null {
  if (!metadataJson) return null;
  try {
    return JSON.stringify(JSON.parse(metadataJson), null, 2);
  } catch {
    return metadataJson;
  }
}

/** A date input (local day) as the first or last instant of that day, in UTC. */
export function dayBoundary(day: string | null, edge: "start" | "end"): string | null {
  if (!day || !/^\d{4}-\d{2}-\d{2}$/.test(day)) return null;
  const date = new Date(`${day}T${edge === "start" ? "00:00:00.000" : "23:59:59.999"}`);
  return Number.isNaN(date.valueOf()) ? null : date.toISOString();
}
