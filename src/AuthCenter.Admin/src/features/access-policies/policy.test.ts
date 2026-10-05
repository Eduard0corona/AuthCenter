import { describe, expect, it } from "vitest";
import type { AccessPolicyRule } from "../../api/types";
import { actionLabels, assuranceLabels, assuranceLevels, dayLabels, diffPolicyRules, policyDays, policyRulePayload, policyRuleSchema, riskLabels, simulationRiskLevels, versionStatusLabels } from "./policy";

describe("policy labels", () => {
  it("names every day, risk, assurance level, action and version state the API uses", () => {
    expect(policyDays.map((day) => dayLabels[day])).toEqual(["Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo"]);
    expect(simulationRiskLevels.map((risk) => riskLabels[risk])).toEqual(["Desconocido", "Bajo", "Medio", "Alto", "Crítico"]);
    expect(assuranceLevels.every((level) => assuranceLabels[level].length > 0)).toBe(true);
    expect([actionLabels.Allow, actionLabels.Deny]).toEqual(["Permitir", "Denegar"]);
    expect([versionStatusLabels.Draft, versionStatusLabels.Published, versionStatusLabels.Archived]).toEqual(["Borrador", "Publicada", "Archivada"]);
  });
});

const baseRule: AccessPolicyRule = {
  id: "11111111-1111-4111-8111-111111111111",
  applicationSystemId: "22222222-2222-4222-8222-222222222222",
  policyVersionId: "33333333-3333-4333-8333-333333333333",
  policyVersionNumber: 1,
  policyVersionStatus: "Published",
  applicationCode: "PARTNER",
  userId: null,
  userEmail: null,
  directoryGroupId: null,
  directoryGroupName: null,
  name: "Allow with MFA",
  priority: 100,
  action: "Allow",
  mfaRequirement: "Required",
  allowTrustedDeviceBypass: false,
  includedIpCidrs: [],
  excludedIpCidrs: [],
  activeFromUtc: null,
  activeUntilUtc: null,
  activeDaysUtc: [],
  dailyStartTimeUtc: null,
  dailyEndTimeUtc: null,
  minimumRiskLevel: null,
  maximumRiskLevel: null,
  requiredAssuranceLevel: "Mfa",
  isActive: true,
  createdAt: "2026-08-13T00:00:00Z",
  updatedAt: null
};

describe("access policy editor", () => {
  it("builds a rule payload with explicit targets, networks and UTC schedule", () => {
    const values = policyRuleSchema.parse({
      name: "Office access", priority: 10, targetType: "group", targetId: "44444444-4444-4444-8444-444444444444",
      action: "Allow", mfaRequirement: "Required", allowTrustedDeviceBypass: false,
      includedIpCidrs: "10.0.0.0/8\n192.168.0.0/16", excludedIpCidrs: "10.10.0.0/16",
      activeFromUtc: "2026-08-13T09:00", activeUntilUtc: "2026-08-13T17:00", activeDaysUtc: ["Thursday"],
      dailyStartTimeUtc: "09:00", dailyEndTimeUtc: "17:00", minimumRiskLevel: "Low", maximumRiskLevel: "High",
      requiredAssuranceLevel: "Mfa", isActive: true
    });
    expect(policyRulePayload(values, baseRule.applicationSystemId, baseRule.policyVersionId, true)).toMatchObject({
      directoryGroupId: "44444444-4444-4444-8444-444444444444",
      userId: null,
      includedIpCidrs: ["10.0.0.0/8", "192.168.0.0/16"],
      activeFromUtc: "2026-08-13T09:00:00.000Z"
    });
  });

  it("detects added, removed and changed priorities while ignoring version identity", () => {
    const draft = [
      { ...baseRule, id: crypto.randomUUID(), policyVersionId: crypto.randomUUID(), policyVersionNumber: 2, policyVersionStatus: "Draft" as const, name: "Require phishing resistant" },
      { ...baseRule, id: crypto.randomUUID(), priority: 200, name: "Fallback" }
    ];
    const published = [baseRule, { ...baseRule, id: crypto.randomUUID(), priority: 300, name: "Legacy" }];
    expect(diffPolicyRules(draft, published)).toEqual([
      { priority: 100, name: "Require phishing resistant", kind: "changed" },
      { priority: 200, name: "Fallback", kind: "added" },
      { priority: 300, name: "Legacy", kind: "removed" }
    ]);
  });

  it("rejects incomplete daily windows and invalid risk ranges", () => {
    const result = policyRuleSchema.safeParse({
      name: "Invalid", priority: 1, targetType: "all", targetId: "", action: "Allow", mfaRequirement: "Optional",
      allowTrustedDeviceBypass: true, includedIpCidrs: "", excludedIpCidrs: "", activeFromUtc: "", activeUntilUtc: "",
      activeDaysUtc: [], dailyStartTimeUtc: "09:00", dailyEndTimeUtc: "", minimumRiskLevel: "Critical", maximumRiskLevel: "Low",
      requiredAssuranceLevel: "Password", isActive: true
    });
    expect(result.success).toBe(false);
  });
});
