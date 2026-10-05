import type { AdminDashboard } from "../../api/types";
import { byAttention, dashboardMetrics, quickActions } from "./dashboard";

const quiet: AdminDashboard = {
  generatedAt: "2026-09-26T12:00:00Z", activeUsers: 1280, inactiveUsers: 42, activeApplications: 7, activeGroups: 18, pendingAccessRequests: 0,
  activeAccessReviews: 0, overdueAccessReviews: 0, pendingAccessReviewItems: 0, separationOfDutiesViolations: 0, activeFederationProviders: 2,
  expiringProvisioningTokens: 0, unverifiedEventHooks: 0, deadLetterDeliveries: 0, failedLoginsLast24Hours: 0, highRiskObservationsLast24Hours: 0
};

describe("quickActions", () => {
  it("offers only what the operator can complete", () => {
    expect(quickActions(new Set(["AUTHCENTER_APPLICATIONS_WRITE"])).map((action) => action.to)).toEqual(["/applications/new"]);
    // A client or an invitation needs an application to choose from.
    expect(quickActions(new Set(["AUTHCENTER_OAUTH_CLIENTS_WRITE", "AUTHCENTER_USERS_WRITE"]))).toEqual([]);
    expect(quickActions(new Set(["AUTHCENTER_OAUTH_CLIENTS_WRITE", "AUTHCENTER_USERS_WRITE", "AUTHCENTER_APPLICATIONS_READ"])).map((action) => action.label))
      .toEqual(["Crear cliente OAuth", "Invitar usuario"]);
    expect(quickActions(new Set(["AUTHCENTER_USERS_READ"]))).toEqual([]);
  });
});

describe("byAttention", () => {
  it("keeps the reading order when nothing needs attention", () => {
    expect(byAttention(dashboardMetrics(quiet)).map((metric) => metric.key)).toEqual(dashboardMetrics(quiet).map((metric) => metric.key));
  });

  it("puts critical indicators first, then the ones that need attention", () => {
    const busy = { ...quiet, pendingAccessRequests: 3, deadLetterDeliveries: 4, failedLoginsLast24Hours: 12, overdueAccessReviews: 1, activeAccessReviews: 1 };
    expect(byAttention(dashboardMetrics(busy)).slice(0, 5).map((metric) => [metric.key, metric.tone])).toEqual([
      ["reviews", "critical"], ["dead-letters", "critical"], ["pending", "attention"], ["failed-logins", "attention"], ["users", "neutral"]
    ]);
  });

  it("names overdue reviews in the detail", () => {
    const review = dashboardMetrics({ ...quiet, activeAccessReviews: 2, pendingAccessReviewItems: 5, overdueAccessReviews: 1 }).find((metric) => metric.key === "reviews");
    expect(review?.detail).toBe("5 accesos por revisar · 1 vencidas");
  });
});
