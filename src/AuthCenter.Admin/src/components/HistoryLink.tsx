import { Link } from "react-router-dom";
import { useSession } from "../auth/session";
import { historyPath } from "../features/system-log/entities";

/** "Ver historial": the System Log filtered to one entity, for operators who can read it. */
export function HistoryLink({ entityName, entityId }: { entityName: string; entityId: string | null | undefined }) {
  const { permissions } = useSession();
  if (!entityId || !permissions.has("AUTHCENTER_AUDIT_LOGS_READ")) return null;
  return <Link className="button button--secondary" to={historyPath(entityName, entityId)}>Ver historial</Link>;
}
