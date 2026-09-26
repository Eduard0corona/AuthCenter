import { useState } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { useSession } from "../auth/session";
import { ErrorBoundary } from "../components/ErrorBoundary";
import { GlobalErrorBanner } from "../components/GlobalErrorBanner";
import { describeEnvironment, describeVersion, useAdminMetadata, useVersion } from "../hooks/usePlatform";

interface NavigationItem {
  label: string;
  to: string;
  permission?: string;
}

interface NavigationGroup {
  label: string;
  items: NavigationItem[];
}

const navigation: NavigationGroup[] = [
  { label: "Inicio", items: [{ label: "Overview", to: "/" }] },
  {
    label: "Directorio",
    items: [
      { label: "Usuarios", to: "/users", permission: "AUTHCENTER_USERS_READ" },
      { label: "Grupos", to: "/groups", permission: "AUTHCENTER_GROUPS_READ" },
      { label: "Esquema de perfil", to: "/profile-schema", permission: "AUTHCENTER_PROFILE_SCHEMAS_READ" }
    ]
  },
  {
    label: "Aplicaciones",
    items: [
      { label: "Aplicaciones", to: "/applications", permission: "AUTHCENTER_APPLICATIONS_READ" },
      { label: "OAuth clients", to: "/oauth-clients", permission: "AUTHCENTER_OAUTH_CLIENTS_READ" },
      { label: "Recursos de API", to: "/api-resources", permission: "AUTHCENTER_OAUTH_CLIENTS_READ" }
    ]
  },
  {
    label: "Lifecycle",
    items: [
      { label: "Provisioning tokens", to: "/provisioning-tokens", permission: "AUTHCENTER_PROVISIONING_READ" },
      { label: "Profile mappings", to: "/profile-mappings", permission: "AUTHCENTER_USERS_READ" },
      { label: "Group rules", to: "/group-rules", permission: "AUTHCENTER_GROUPS_READ" }
    ]
  },
  { label: "Federación", items: [{ label: "Proveedores y routing", to: "/federation", permission: "AUTHCENTER_FEDERATION_READ" }] },
  {
    label: "Seguridad",
    items: [
      { label: "Roles", to: "/roles", permission: "AUTHCENTER_ROLES_READ" },
      { label: "Permisos", to: "/permissions", permission: "AUTHCENTER_PERMISSIONS_READ" },
      { label: "Políticas de acceso", to: "/access-policies", permission: "AUTHCENTER_ACCESS_POLICIES_READ" }
    ]
  },
  {
    label: "Operación",
    items: [
      { label: "Event Hooks", to: "/event-hooks", permission: "AUTHCENTER_EVENT_HOOKS_READ" },
      { label: "System Log", to: "/system-log", permission: "AUTHCENTER_AUDIT_LOGS_READ" }
    ]
  }
];

export function AppShell() {
  const { user, permissions, signOut, signingOut } = useSession();
  const [menuOpen, setMenuOpen] = useState(false);
  const location = useLocation();
  const environment = describeEnvironment(useAdminMetadata().data?.environmentName);
  const version = describeVersion(useVersion().data);

  return (
    <div className="admin-shell">
      <aside id="admin-navigation" className={`sidebar ${menuOpen ? "sidebar--open" : ""}`} aria-label="Navegación administrativa">
        <div className="brand-lockup">
          <span className="brand-mark" aria-hidden="true">A</span>
          <div><strong>AuthCenter</strong><span>Admin Console</span></div>
        </div>
        <nav>
          {navigation.map((group) => {
            const visibleItems = group.items.filter((item) => !item.permission || permissions.has(item.permission));
            if (visibleItems.length === 0) return null;
            return (
              <section className="nav-group" key={group.label} aria-labelledby={`nav-${group.label}`}>
                <h2 id={`nav-${group.label}`}>{group.label}</h2>
                {visibleItems.map((item) => (
                  <NavLink key={item.to} to={item.to} end={item.to === "/"} onClick={() => setMenuOpen(false)}>
                    <span aria-hidden="true" className="nav-dot" />{item.label}
                  </NavLink>
                ))}
              </section>
            );
          })}
        </nav>
        <div className="sidebar__footer">
          <span className="avatar" aria-hidden="true">{initials(user.name ?? user.email ?? "AC")}</span>
          <div><strong>{user.name ?? user.email}</strong><span>Sesión administrativa</span>{version ? <span className="sidebar__version">AuthCenter {version}</span> : null}</div>
        </div>
      </aside>
      {menuOpen ? <button className="sidebar-scrim" aria-label="Cerrar navegación" onClick={() => setMenuOpen(false)} /> : null}
      <div className="workspace">
        <header className="topbar">
          <button className="icon-button mobile-only" type="button" onClick={() => setMenuOpen(true)} aria-expanded={menuOpen} aria-controls="admin-navigation">☰<span className="sr-only">Abrir navegación</span></button>
          {environment ? <div className={`environment-pill environment-pill--${environment.tone}`}><span aria-hidden="true" /> <span className="sr-only">Entorno: </span>{environment.label}</div> : <div />}
          <div className="topbar__actions">
            <a className="button button--quiet" href="/portal">Mi cuenta</a>
            <button className="button button--secondary" type="button" onClick={signOut} disabled={signingOut}>{signingOut ? "Cerrando…" : "Cerrar sesión"}</button>
          </div>
        </header>
        <main id="main-content" className="content" key={location.pathname}>
          <GlobalErrorBanner />
          <ErrorBoundary resetKey={location.pathname}><Outlet /></ErrorBoundary>
        </main>
      </div>
    </div>
  );
}

function initials(value: string): string {
  return value.split(/\s|@/).filter(Boolean).slice(0, 2).map((part) => part[0]?.toUpperCase()).join("") || "AC";
}
