import { useQuery } from "@tanstack/react-query";
import { apiRequest } from "./client";
import type { ApplicationSummary, DirectoryGroupSummary, PagedResult, PermissionSummary, RoleSummary } from "./types";

const PAGE_SIZE = 100;
const MAX_PAGES = 50;

/**
 * Every item of a paged list, page by page (the API serves at most 100 per page), so selectors
 * never silently stop at the first page.
 */
export async function fetchAllPages<T>(path: string, signal?: AbortSignal): Promise<T[]> {
  const items: T[] = [];
  const separator = path.includes("?") ? "&" : "?";
  for (let page = 1; page <= MAX_PAGES; page += 1) {
    const result = await apiRequest<PagedResult<T>>(`${path}${separator}page=${page}&pageSize=${PAGE_SIZE}`, { signal: signal ?? null });
    items.push(...result.items);
    if (result.items.length === 0 || page >= result.totalPages) break;
  }
  return items;
}

/** Every item as a single page, for selectors written against the paged shape. */
export async function fetchAllAsPage<T>(path: string, signal?: AbortSignal): Promise<PagedResult<T>> {
  const items = await fetchAllPages<T>(path, signal);
  return { items, totalCount: items.length, page: 1, pageSize: items.length, totalPages: 1 };
}

// The keys start with the list's own key, so the invalidation after a create or an update of that
// resource refreshes the selectors too.

/** All applications, shared by the selectors of every page. */
export function useApplicationsCatalog(enabled = true) {
  return useQuery({
    queryKey: ["applications", "catalog"],
    enabled,
    queryFn: ({ signal }) => fetchAllPages<ApplicationSummary>("/api/applications", signal)
  });
}

/** All roles, optionally of one application. */
export function useRolesCatalog(applicationSystemId?: string | null, enabled = true) {
  return useQuery({
    queryKey: ["roles", "catalog", applicationSystemId ?? "all"],
    enabled,
    queryFn: ({ signal }) => fetchAllPages<RoleSummary>(applicationSystemId ? `/api/roles?applicationSystemId=${applicationSystemId}` : "/api/roles", signal)
  });
}

/** All directory groups. */
export function useGroupsCatalog(enabled = true) {
  return useQuery({
    queryKey: ["groups", "catalog"],
    enabled,
    queryFn: ({ signal }) => fetchAllPages<DirectoryGroupSummary>("/api/groups", signal)
  });
}

/** All permissions of one application. */
export function usePermissionsCatalog(applicationSystemId: string | null | undefined, enabled = true) {
  return useQuery({
    queryKey: ["permissions", "catalog", applicationSystemId ?? ""],
    enabled: enabled && Boolean(applicationSystemId),
    queryFn: ({ signal }) => fetchAllPages<PermissionSummary>(`/api/applications/${applicationSystemId}/permissions`, signal)
  });
}
