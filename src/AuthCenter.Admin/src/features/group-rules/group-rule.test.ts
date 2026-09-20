import { describe, expect, it } from "vitest";
import { convertExpectedValue, describeRule, groupRuleCreatePayload, groupRuleFromResponse, groupRuleSchema, groupRuleUpdatePayload } from "./group-rule";

const values = {
  directoryGroupId: "55555555-5555-4555-8555-555555555555",
  profileAttributeDefinitionId: "66666666-6666-4666-8666-666666666666",
  operator: "eq" as const,
  expectedValue: "Ingeniería",
  isActive: false
};

describe("group rule form", () => {
  it("converts the expected value to the attribute's JSON type", () => {
    expect(convertExpectedValue(" 42 ", "Integer")).toEqual({ ok: true, value: 42 });
    expect(convertExpectedValue("4.5", "Decimal")).toEqual({ ok: true, value: 4.5 });
    expect(convertExpectedValue("true", "Boolean")).toEqual({ ok: true, value: true });
    expect(convertExpectedValue("2026-08-13", "Date")).toEqual({ ok: true, value: "2026-08-13" });
    expect(convertExpectedValue("Ingeniería", "String")).toEqual({ ok: true, value: "Ingeniería" });
  });

  it("rejects values that cannot match the attribute type", () => {
    expect(convertExpectedValue("4.5", "Integer").ok).toBe(false);
    expect(convertExpectedValue("yes", "Boolean").ok).toBe(false);
    expect(convertExpectedValue("not-a-date", "DateTime").ok).toBe(false);
    expect(convertExpectedValue("   ", "String").ok).toBe(false);
  });

  it("builds create and versioned update requests", () => {
    expect(groupRuleCreatePayload(values, "Ingeniería")).toEqual({ directoryGroupId: values.directoryGroupId, profileAttributeDefinitionId: values.profileAttributeDefinitionId, operator: "eq", expectedValue: "Ingeniería" });
    expect(groupRuleUpdatePayload(values, 7, 3)).toEqual({ profileAttributeDefinitionId: values.profileAttributeDefinitionId, operator: "eq", expectedValue: 7, isActive: false, version: 3 });
    expect(groupRuleSchema.safeParse({ ...values, operator: "contains" }).success).toBe(false);
  });

  it("round-trips a stored rule into editable form values", () => {
    const form = groupRuleFromResponse({ id: "r", directoryGroupId: values.directoryGroupId, groupName: "Ops", profileAttributeDefinitionId: values.profileAttributeDefinitionId, attributeName: "level", operator: "eq", expectedValue: 3, isActive: true, createdAt: "2026-08-13T00:00:00Z", version: 1 });
    expect(form).toMatchObject({ operator: "eq", expectedValue: "3", isActive: true });
    expect(describeRule({ attributeName: "level", operator: "eq", expectedValue: 3 })).toBe("level = 3");
    expect(describeRule({ attributeName: "department", operator: "eq", expectedValue: "Ops" })).toBe("department = \"Ops\"");
  });
});
