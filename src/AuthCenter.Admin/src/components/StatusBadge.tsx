export function StatusBadge({ active, activeLabel = "Activo", inactiveLabel = "Inactivo" }: { active: boolean; activeLabel?: string; inactiveLabel?: string }) {
  return <span className={`status-badge ${active ? "status-badge--active" : "status-badge--inactive"}`}>{active ? activeLabel : inactiveLabel}</span>;
}
