import { lazy, Suspense } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { PageState } from "../components/PageState";
import { AppShell } from "./AppShell";
import { PermissionRoute } from "./PermissionRoute";

const DashboardPage = lazy(() => import("../features/dashboard/DashboardPage"));
const UsersPage = lazy(() => import("../features/users/UsersPage"));
const UserEditorPage = lazy(() => import("../features/users/UserEditorPage"));
const UserProvisioningPage = lazy(() => import("../features/users/UserProvisioningPage"));
const ApplicationsPage = lazy(() => import("../features/applications/ApplicationsPage"));
const ApplicationEditorPage = lazy(() => import("../features/applications/ApplicationEditorPage"));
const OAuthClientsPage = lazy(() => import("../features/oauth-clients/OAuthClientsPage"));
const OAuthClientEditorPage = lazy(() => import("../features/oauth-clients/OAuthClientEditorPage"));
const ProvisioningTokensPage = lazy(() => import("../features/provisioning-tokens/ProvisioningTokensPage"));
const ProvisioningTokenEditorPage = lazy(() => import("../features/provisioning-tokens/ProvisioningTokenEditorPage"));
const ProfileMappingsPage = lazy(() => import("../features/profile-mappings/ProfileMappingsPage"));
const ProfileMappingEditorPage = lazy(() => import("../features/profile-mappings/ProfileMappingEditorPage"));
const GroupRulesPage = lazy(() => import("../features/group-rules/GroupRulesPage"));
const GroupRuleEditorPage = lazy(() => import("../features/group-rules/GroupRuleEditorPage"));
const FederationPage = lazy(() => import("../features/federation/FederationPage"));
const ProviderEditorPage = lazy(() => import("../features/federation/ProviderEditorPage"));
const AccessPoliciesPage = lazy(() => import("../features/access-policies/AccessPoliciesPage"));
const AccessPolicyEditorPage = lazy(() => import("../features/access-policies/AccessPolicyEditorPage"));
const RolesPage = lazy(() => import("../features/roles/RolesPage"));
const RoleEditorPage = lazy(() => import("../features/roles/RoleEditorPage"));
const PermissionsPage = lazy(() => import("../features/permissions/PermissionsPage"));
const PermissionEditorPage = lazy(() => import("../features/permissions/PermissionEditorPage"));
const GroupsPage = lazy(() => import("../features/groups/GroupsPage"));
const GroupEditorPage = lazy(() => import("../features/groups/GroupEditorPage"));
const SystemLogPage = lazy(() => import("../features/system-log/SystemLogPage"));
const EventHooksPage = lazy(() => import("../features/event-hooks/EventHooksPage"));
const EventHookEditorPage = lazy(() => import("../features/event-hooks/EventHookEditorPage"));
const EventDeliveriesPage = lazy(() => import("../features/event-hooks/EventDeliveriesPage"));

const loading = <PageState title="Cargando módulo" detail="Estamos preparando esta sección." busy />;

export function AppRouter() {
  return (
    <Suspense fallback={loading}>
      <Routes>
        <Route element={<AppShell />}>
          <Route index element={<DashboardPage />} />
          <Route path="users" element={<PermissionRoute permission="AUTHCENTER_USERS_READ"><UsersPage /></PermissionRoute>} />
          <Route path="users/new" element={<PermissionRoute permission="AUTHCENTER_USERS_WRITE"><UserProvisioningPage mode="create" /></PermissionRoute>} />
          <Route path="users/invite" element={<PermissionRoute permission="AUTHCENTER_USERS_WRITE"><UserProvisioningPage mode="invite" /></PermissionRoute>} />
          <Route path="users/:userId" element={<PermissionRoute permission="AUTHCENTER_USERS_READ"><UserEditorPage /></PermissionRoute>} />
          <Route path="applications" element={<PermissionRoute permission="AUTHCENTER_APPLICATIONS_READ"><ApplicationsPage /></PermissionRoute>} />
          <Route path="applications/new" element={<PermissionRoute permission="AUTHCENTER_APPLICATIONS_WRITE"><ApplicationEditorPage create /></PermissionRoute>} />
          <Route path="applications/:applicationId" element={<PermissionRoute permission="AUTHCENTER_APPLICATIONS_READ"><ApplicationEditorPage /></PermissionRoute>} />
          <Route path="oauth-clients" element={<PermissionRoute permission="AUTHCENTER_OAUTH_CLIENTS_READ"><OAuthClientsPage /></PermissionRoute>} />
          <Route path="oauth-clients/new" element={<PermissionRoute permission="AUTHCENTER_OAUTH_CLIENTS_WRITE"><OAuthClientEditorPage create /></PermissionRoute>} />
          <Route path="oauth-clients/:clientId" element={<PermissionRoute permission="AUTHCENTER_OAUTH_CLIENTS_READ"><OAuthClientEditorPage /></PermissionRoute>} />
          <Route path="provisioning-tokens" element={<PermissionRoute permission="AUTHCENTER_PROVISIONING_READ"><ProvisioningTokensPage /></PermissionRoute>} />
          <Route path="provisioning-tokens/new" element={<PermissionRoute permission="AUTHCENTER_PROVISIONING_WRITE"><ProvisioningTokenEditorPage create /></PermissionRoute>} />
          <Route path="provisioning-tokens/:tokenId" element={<PermissionRoute permission="AUTHCENTER_PROVISIONING_READ"><ProvisioningTokenEditorPage /></PermissionRoute>} />
          <Route path="profile-mappings" element={<PermissionRoute permission="AUTHCENTER_USERS_READ"><ProfileMappingsPage /></PermissionRoute>} />
          <Route path="profile-mappings/new" element={<PermissionRoute permission="AUTHCENTER_USERS_WRITE"><ProfileMappingEditorPage create /></PermissionRoute>} />
          <Route path="profile-mappings/:mappingId" element={<PermissionRoute permission="AUTHCENTER_USERS_READ"><ProfileMappingEditorPage /></PermissionRoute>} />
          <Route path="group-rules" element={<PermissionRoute permission="AUTHCENTER_GROUPS_READ"><GroupRulesPage /></PermissionRoute>} />
          <Route path="group-rules/new" element={<PermissionRoute permission="AUTHCENTER_GROUPS_WRITE"><GroupRuleEditorPage create /></PermissionRoute>} />
          <Route path="group-rules/:ruleId" element={<PermissionRoute permission="AUTHCENTER_GROUPS_READ"><GroupRuleEditorPage /></PermissionRoute>} />
          <Route path="federation" element={<PermissionRoute permission="AUTHCENTER_FEDERATION_READ"><FederationPage /></PermissionRoute>} />
          <Route path="federation/providers/new" element={<PermissionRoute permission="AUTHCENTER_FEDERATION_WRITE"><ProviderEditorPage create /></PermissionRoute>} />
          <Route path="federation/providers/:providerId" element={<PermissionRoute permission="AUTHCENTER_FEDERATION_READ"><ProviderEditorPage /></PermissionRoute>} />
          <Route path="access-policies" element={<PermissionRoute permission="AUTHCENTER_ACCESS_POLICIES_READ"><AccessPoliciesPage /></PermissionRoute>} />
          <Route path="access-policies/:applicationId" element={<PermissionRoute permission="AUTHCENTER_ACCESS_POLICIES_READ"><AccessPolicyEditorPage /></PermissionRoute>} />
          <Route path="roles" element={<PermissionRoute permission="AUTHCENTER_ROLES_READ"><RolesPage /></PermissionRoute>} />
          <Route path="roles/new" element={<PermissionRoute permission="AUTHCENTER_ROLES_WRITE"><RoleEditorPage create /></PermissionRoute>} />
          <Route path="roles/:roleId" element={<PermissionRoute permission="AUTHCENTER_ROLES_READ"><RoleEditorPage /></PermissionRoute>} />
          <Route path="permissions" element={<PermissionRoute permission="AUTHCENTER_PERMISSIONS_READ"><PermissionsPage /></PermissionRoute>} />
          <Route path="permissions/new" element={<PermissionRoute permission="AUTHCENTER_PERMISSIONS_WRITE"><PermissionEditorPage create /></PermissionRoute>} />
          <Route path="permissions/:permissionId" element={<PermissionRoute permission="AUTHCENTER_PERMISSIONS_READ"><PermissionEditorPage /></PermissionRoute>} />
          <Route path="system-log" element={<PermissionRoute permission="AUTHCENTER_AUDIT_LOGS_READ"><SystemLogPage /></PermissionRoute>} />
          <Route path="event-hooks" element={<PermissionRoute permission="AUTHCENTER_EVENT_HOOKS_READ"><EventHooksPage /></PermissionRoute>} />
          <Route path="event-hooks/new" element={<PermissionRoute permission="AUTHCENTER_EVENT_HOOKS_WRITE"><EventHookEditorPage create /></PermissionRoute>} />
          <Route path="event-hooks/deliveries" element={<PermissionRoute permission="AUTHCENTER_EVENT_HOOKS_READ"><EventDeliveriesPage /></PermissionRoute>} />
          <Route path="event-hooks/:hookId" element={<PermissionRoute permission="AUTHCENTER_EVENT_HOOKS_READ"><EventHookEditorPage /></PermissionRoute>} />
          <Route path="groups" element={<PermissionRoute permission="AUTHCENTER_GROUPS_READ"><GroupsPage /></PermissionRoute>} />
          <Route path="groups/new" element={<PermissionRoute permission="AUTHCENTER_GROUPS_WRITE"><GroupEditorPage create /></PermissionRoute>} />
          <Route path="groups/:groupId" element={<PermissionRoute permission="AUTHCENTER_GROUPS_READ"><GroupEditorPage /></PermissionRoute>} />
          <Route path="404" element={<PageState title="Ruta no encontrada" detail="La sección solicitada no existe o cambió de ubicación." />} />
          <Route path="*" element={<Navigate to="/404" replace />} />
        </Route>
      </Routes>
    </Suspense>
  );
}
