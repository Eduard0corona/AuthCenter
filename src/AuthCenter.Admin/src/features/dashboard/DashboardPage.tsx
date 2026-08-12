import { Link } from "react-router-dom";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";

const modules = [
  { title: "Usuarios", description: "Directorio, estado y acceso efectivo.", to: "/users", permission: "AUTHCENTER_USERS_READ", accent: "blue" },
  { title: "Aplicaciones", description: "Configuración, registro y branding.", to: "/applications", permission: "AUTHCENTER_APPLICATIONS_READ", accent: "violet" },
  { title: "Roles", description: "RBAC, permisos y alcance por aplicación.", to: "/roles", permission: "AUTHCENTER_ROLES_READ", accent: "blue" },
  { title: "Permisos", description: "Catálogo de capacidades por aplicación.", to: "/permissions", permission: "AUTHCENTER_PERMISSIONS_READ", accent: "emerald" },
  { title: "System Log", description: "Eventos administrativos y trazabilidad.", to: "/system-log", permission: "AUTHCENTER_AUDIT_LOGS_READ", accent: "amber" },
  { title: "Event Hooks", description: "Entregas, errores y replay controlado.", to: "/event-hooks", permission: "AUTHCENTER_APPLICATIONS_WRITE", accent: "emerald" }
];

export default function DashboardPage() {
  const { user, permissions } = useSession();
  return (
    <>
      <PageHeader eyebrow="Overview" title={`Hola, ${user.name?.split(" ")[0] ?? "operador"}`} description="Opera AuthCenter desde módulos cargados bajo demanda. Cada acción vuelve a autorizarse en el servidor." />
      <section className="module-grid" aria-label="Módulos disponibles">
        {modules.filter((module) => permissions.has(module.permission)).map((module) => (
          <Link className={`module-card module-card--${module.accent}`} to={module.to} key={module.to}>
            <span className="module-card__icon" aria-hidden="true">↗</span>
            <h2>{module.title}</h2><p>{module.description}</p><strong>Abrir módulo</strong>
          </Link>
        ))}
      </section>
      <section className="security-note">
        <div className="security-note__mark" aria-hidden="true">✓</div>
        <div><h2>Frontera de seguridad server-side</h2><p>La consola oculta acciones no autorizadas para reducir ruido, pero ninguna decisión depende del navegador. La API valida de nuevo sesión, CSRF y permisos.</p></div>
      </section>
    </>
  );
}
