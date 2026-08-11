import { useState } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { useSession } from "../auth/session";

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
      { label: "Grupos", to: "/groups", permission: "AUTHCENTER_GROUPS_READ" }
    ]
  },
  {
    label: "Aplicaciones",
    items: [{ label: "Aplicaciones", to: "/applications", permission: "AUTHCENTER_APPLICATIONS_READ" }]
  },
  {
    label: "Operación",
    items: [
      { label: "Event Hooks", to: "/event-hooks", permission: "AUTHCENTER_APPLICATIONS_WRITE" },
      { label: "System Log", to: "/system-log", permission: "AUTHCENTER_AUDIT_LOGS_READ" }
    ]
  }
];

export function AppShell() {
  const { user, permissions, signOut, signingOut } = useSession();
  const [menuOpen, setMenuOpen] = useState(false);
  const location = useLocation();

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
          <div><strong>{user.name ?? user.email}</strong><span>Sesión administrativa</span></div>
        </div>
      </aside>
      {menuOpen ? <button className="sidebar-scrim" aria-label="Cerrar navegación" onClick={() => setMenuOpen(false)} /> : null}
      <div className="workspace">
        <header className="topbar">
          <button className="icon-button mobile-only" type="button" onClick={() => setMenuOpen(true)} aria-expanded={menuOpen} aria-controls="admin-navigation">☰<span className="sr-only">Abrir navegación</span></button>
          <div className="environment-pill"><span aria-hidden="true" /> Producción</div>
          <div className="topbar__actions">
            <a className="button button--quiet" href="/portal">Mi cuenta</a>
            <button className="button button--secondary" type="button" onClick={signOut} disabled={signingOut}>{signingOut ? "Cerrando…" : "Cerrar sesión"}</button>
          </div>
        </header>
        <main id="main-content" className="content" key={location.pathname}>
          <Outlet />
        </main>
      </div>
    </div>
  );
}

function initials(value: string): string {
  return value.split(/\s|@/).filter(Boolean).slice(0, 2).map((part) => part[0]?.toUpperCase()).join("") || "AC";
}
