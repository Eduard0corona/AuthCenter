interface PaginationProps {
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
}

export function Pagination({ page, pageSize, totalCount, totalPages, onPageChange, onPageSizeChange }: PaginationProps) {
  const start = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
  const end = Math.min(page * pageSize, totalCount);
  return (
    <nav className="pagination" aria-label="Paginación">
      <p>{start}–{end} de {totalCount}</p>
      <label>
        <span>Por página</span>
        <select value={pageSize} onChange={(event) => onPageSizeChange(Number(event.target.value))}>
          {[20, 50, 100].map((size) => <option value={size} key={size}>{size}</option>)}
        </select>
      </label>
      <div className="button-group">
        <button className="button button--secondary" type="button" onClick={() => onPageChange(page - 1)} disabled={page <= 1}>Anterior</button>
        <span aria-live="polite">Página {page} de {Math.max(totalPages, 1)}</span>
        <button className="button button--secondary" type="button" onClick={() => onPageChange(page + 1)} disabled={page >= totalPages}>Siguiente</button>
      </div>
    </nav>
  );
}
