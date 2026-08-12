import { lazy, Suspense } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { PageState } from "../components/PageState";
import { AppShell } from "./AppShell";
import { PermissionRoute } from "./PermissionRoute";

const DashboardPage = lazy(() => import("../features/dashboard/DashboardPage"));
const UsersPage = lazy(() => import("../features/users/UsersPage"));
const ApplicationsPage = lazy(() => import("../features/applications/ApplicationsPage"));
const ApplicationEditorPage = lazy(() => import("../features/applications/ApplicationEditorPage"));
const SystemLogPage = lazy(() => import("../features/system-log/SystemLogPage"));
const EventHooksPage = lazy(() => import("../features/event-hooks/EventHooksPage"));
const ComingSoonPage = lazy(() => import("../features/coming-soon/ComingSoonPage"));

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
          <Route path="system-log" element={<PermissionRoute permission="AUTHCENTER_AUDIT_LOGS_READ"><SystemLogPage /></PermissionRoute>} />
          <Route path="event-hooks" element={<PermissionRoute permission="AUTHCENTER_APPLICATIONS_WRITE"><EventHooksPage /></PermissionRoute>} />
          <Route path="groups" element={<ComingSoonPage title="Grupos" phase="Fase B" />} />
          <Route path="404" element={<PageState title="Ruta no encontrada" detail="La sección solicitada no existe o cambió de ubicación." />} />
          <Route path="*" element={<Navigate to="/404" replace />} />
        </Route>
      </Routes>
    </Suspense>
  );
}
