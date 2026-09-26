import { useQuery } from "@tanstack/react-query";
import { Link, useSearchParams } from "react-router-dom";
import { apiRequest } from "../../api/client";
import { errorMessage } from "../../api/errors";
import type { ProfileAttributeDefinition } from "../../api/types";
import { useSession } from "../../auth/session";
import { PageHeader } from "../../components/PageHeader";
import { PageState } from "../../components/PageState";
import { StatusBadge } from "../../components/StatusBadge";
import { dataTypeLabel, describeConstraints } from "./profile-schema";

export default function ProfileSchemaPage() {
  const { permissions } = useSession();
  const canWrite = permissions.has("AUTHCENTER_PROFILE_SCHEMAS_WRITE");
  const [params, setParams] = useSearchParams();
  const includeInactive = params.get("inactive") === "true";
  const schema = useQuery({
    queryKey: ["profile-schema", includeInactive ? "all" : "active"],
    queryFn: ({ signal }) => apiRequest<ProfileAttributeDefinition[]>(`/api/profile-schema${includeInactive ? "?includeInactive=true" : ""}`, { signal })
  });

  return (
    <>
      <PageHeader
        eyebrow="Directorio"
        title="Esquema de perfil"
        description="Atributos personalizados del perfil universal: tipos, valores permitidos y reglas que usan los profile mappings, las group rules y el routing de federación."
        actions={canWrite ? <Link className="button" to="/profile-schema/new">Nuevo atributo</Link> : undefined}
      />
      <section className="toolbar" aria-label="Filtros del esquema">
        <label className="checkbox-field"><input type="checkbox" checked={includeInactive} onChange={(event) => setParams(event.target.checked ? { inactive: "true" } : {}, { replace: true })} /><span>Mostrar atributos desactivados</span></label>
      </section>
      {schema.isPending ? <PageState title="Cargando el esquema" busy /> : null}
      {schema.isError ? <PageState title="No pudimos cargar el esquema" detail={errorMessage(schema.error)} tone="error" action={<button className="button" type="button" onClick={() => void schema.refetch()}>Reintentar</button>} /> : null}
      {schema.data && schema.data.length === 0 ? <PageState title="Sin atributos personalizados" detail="El perfil sólo tiene los campos integrados (nombre, correo, foto)." action={canWrite ? <Link className="button" to="/profile-schema/new">Crear el primer atributo</Link> : undefined} /> : null}
      {schema.data && schema.data.length > 0 ? (
        <div className="data-table" tabIndex={0} role="region" aria-label="Atributos del perfil, desplazamiento horizontal">
          <table>
            <caption className="sr-only">Atributos del perfil</caption>
            <thead><tr><th scope="col">Atributo</th><th scope="col">Tipo</th><th scope="col">Restricciones</th><th scope="col">Estado</th><th scope="col"><span className="sr-only">Acciones</span></th></tr></thead>
            <tbody>{schema.data.map((definition) => <tr key={definition.id}>
              <td><strong>{definition.displayName}</strong><span className="cell-detail mono">{definition.key}</span></td>
              <td>{dataTypeLabel(definition.dataType)}{definition.isRequired ? <span className="tag tag--warning">Obligatorio</span> : null}</td>
              <td>{describeConstraints(definition)}</td>
              <td><StatusBadge active={definition.isActive} activeLabel="Activo" inactiveLabel="Desactivado" /></td>
              <td className="table-action"><Link className="button button--small button--secondary" to={`/profile-schema/${definition.id}`}>Abrir<span className="sr-only"> {definition.displayName}</span></Link></td>
            </tr>)}</tbody>
          </table>
        </div>
      ) : null}
    </>
  );
}
