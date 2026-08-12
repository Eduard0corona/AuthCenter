import { lazy, Suspense } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { PageState } from "../components/PageState";
import { AppShell } from "./AppShell";
import { PermissionRoute } from "./PermissionRoute";

const DashboardPage = lazy(() => import("../features/dashboard/DashboardPage"));
const UsersPage = lazy(() => import("../features/users/UsersPage"));
const ApplicationsPage = lazy(() => import("../features/applications/ApplicationsPage"));
const ApplicationEditorPage = lazy(() => import("../features/applications/ApplicationEditorPage"));
const RolesPage = lazy(() => import("../features/roles/RolesPage"));
const RoleEditorPage = lazy(() => import("../features/roles/RoleEditorPage"));
const PermissionsPage = lazy(() => import("../features/permissions/PermissionsPage"));
const PermissionEditorPage = lazy(() => import("../features/permissions/PermissionEditorPage"));
const GroupsPage = lazy(() => import("../features/groups/GroupsPage"));
const GroupEditorPage = lazy(() => import("../features/groups/GroupEditorPage"));
const SystemLogPage = lazy(() => import("../features/system-log/SystemLogPage"));
const EventHooksPage = lazy(() => import("../features/event-hooks/EventHooksPage"));

const loading = <PageState title="Cargando módulo" detail="Estamos preparando esta sección." busy />;

export function AppRouter() {
  return (
    <Suspense fallback={loading}>
      <Routes>
        <Route element={<AppShell />}>
          <Route index element={<DashboardPage />} />
          <Route path="users" element={<PermissionRoute permission="AUTHCENTER_USERS_READ"><UsersPage /></PermissionRoute>} />
          <Route path="applications" element={<PermissionRoute permission="AUTHCENTER_APPLICATIONS_READ"><ApplicationsPage /></PermissionRoute>} />
          <Route path="applications/new" element={<PermissionRoute permission="AUTHCENTER_APPLICATIONS_WRITE"><ApplicationEditorPage create /></PermissionRoute>} />
          <Route path="applications/:applicationId" element={<PermissionRoute permission="AUTHCENTER_APPLICATIONS_READ"><ApplicationEditorPage /></PermissionRoute>} />
          <Route path="roles" element={<PermissionRoute permission="AUTHCENTER_ROLES_READ"><RolesPage /></PermissionRoute>} />
          <Route path="roles/new" element={<PermissionRoute permission="AUTHCENTER_ROLES_WRITE"><RoleEditorPage create /></PermissionRoute>} />
          <Route path="roles/:roleId" element={<PermissionRoute permission="AUTHCENTER_ROLES_READ"><RoleEditorPage /></PermissionRoute>} />
          <Route path="permissions" element={<PermissionRoute permission="AUTHCENTER_PERMISSIONS_READ"><PermissionsPage /></PermissionRoute>} />
          <Route path="permissions/new" element={<PermissionRoute permission="AUTHCENTER_PERMISSIONS_WRITE"><PermissionEditorPage create /></PermissionRoute>} />
          <Route path="permissions/:permissionId" element={<PermissionRoute permission="AUTHCENTER_PERMISSIONS_READ"><PermissionEditorPage /></PermissionRoute>} />
          <Route path="system-log" element={<PermissionRoute permission="AUTHCENTER_AUDIT_LOGS_READ"><SystemLogPage /></PermissionRoute>} />
          <Route path="event-hooks" element={<PermissionRoute permission="AUTHCENTER_APPLICATIONS_WRITE"><EventHooksPage /></PermissionRoute>} />
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
