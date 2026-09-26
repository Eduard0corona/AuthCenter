import { describe, expect, it } from "vitest";
import { convertExpectedValue, describeRule, groupRuleCreatePayload, groupRuleFromResponse, groupRuleSchema, groupRuleUpdatePayload, operatorsFor } from "./group-rule";

const values = {
  directoryGroupId: "55555555-5555-4555-8555-555555555555",
  profileAttributeDefinitionId: "66666666-6666-4666-8666-666666666666",
  operator: "eq" as const,
  expectedValue: "Ingeniería",
  isActive: false
};

const stored = { id: "r", directoryGroupId: values.directoryGroupId, groupName: "Ops", profileAttributeDefinitionId: values.profileAttributeDefinitionId, attributeName: "level", isActive: true, createdAt: "2026-08-13T00:00:00Z", version: 1 };

describe("group rule form", () => {
  it("converts the expected value to the attribute's JSON type", () => {
    expect(convertExpectedValue(" 42 ", "Integer")).toEqual({ ok: true, value: 42 });
    expect(convertExpectedValue("4.5", "Decimal")).toEqual({ ok: true, value: 4.5 });
    expect(convertExpectedValue("true", "Boolean")).toEqual({ ok: true, value: true });
    expect(convertExpectedValue("2026-08-13", "Date")).toEqual({ ok: true, value: "2026-08-13" });
    expect(convertExpectedValue("2026-08-13T09:00:00Z", "DateTime")).toEqual({ ok: true, value: "2026-08-13T09:00:00Z" });
    expect(convertExpectedValue("Ingeniería", "String")).toEqual({ ok: true, value: "Ingeniería" });
  });

  it("rejects values that cannot match the attribute type", () => {
    expect(convertExpectedValue("4.5", "Integer").ok).toBe(false);
    expect(convertExpectedValue("yes", "Boolean").ok).toBe(false);
    expect(convertExpectedValue("not-a-date", "DateTime").ok).toBe(false);
    expect(convertExpectedValue("2026-08-13T09:00:00", "DateTime").ok).toBe(false);
    expect(convertExpectedValue("13/08/2026", "Date").ok).toBe(false);
    expect(convertExpectedValue("   ", "String").ok).toBe(false);
  });

  it("builds a list for in, one typed value per line, and true for exists", () => {
    expect(convertExpectedValue("2\n 3\n\n3", "Integer", "in")).toEqual({ ok: true, value: [2, 3] });
    expect(convertExpectedValue("Ventas\nOperaciones, Norte", "String", "in")).toEqual({ ok: true, value: ["Ventas", "Operaciones, Norte"] });
    expect(convertExpectedValue("2\nmany", "Integer", "in")).toEqual({ ok: false, error: "many: El atributo es entero; usa solo dígitos." });
    expect(convertExpectedValue("\n", "Integer", "in").ok).toBe(false);
    expect(convertExpectedValue("", "Date", "exists")).toEqual({ ok: true, value: true });
  });

  it("offers only the operators the attribute's type supports", () => {
    expect(operatorsFor("String")).toEqual(["eq", "ne", "in", "contains", "startsWith", "exists"]);
    expect(operatorsFor("Integer")).toEqual(["eq", "ne", "in", "gt", "gte", "lt", "lte", "exists"]);
    expect(operatorsFor("Boolean")).toEqual(["eq", "ne", "in", "exists"]);
    expect(operatorsFor(undefined)).toEqual(["eq", "ne", "in", "exists"]);
  });

  it("builds create and versioned update requests", () => {
    expect(groupRuleCreatePayload(values, "Ingeniería")).toEqual({ directoryGroupId: values.directoryGroupId, profileAttributeDefinitionId: values.profileAttributeDefinitionId, operator: "eq", expectedValue: "Ingeniería" });
    expect(groupRuleUpdatePayload({ ...values, operator: "in" }, [7, 8], 3)).toEqual({ profileAttributeDefinitionId: values.profileAttributeDefinitionId, operator: "in", expectedValue: [7, 8], isActive: false, version: 3 });
    expect(groupRuleSchema.safeParse({ ...values, operator: "regex" }).success).toBe(false);
    expect(groupRuleSchema.safeParse({ ...values, expectedValue: "" }).success).toBe(false);
    expect(groupRuleSchema.safeParse({ ...values, operator: "exists", expectedValue: "" }).success).toBe(true);
  });

  it("round-trips a stored rule into editable form values", () => {
    expect(groupRuleFromResponse({ ...stored, operator: "eq", expectedValue: 3 })).toMatchObject({ operator: "eq", expectedValue: "3", isActive: true });
    expect(groupRuleFromResponse({ ...stored, operator: "in", expectedValue: [2, 3] })).toMatchObject({ operator: "in", expectedValue: "2\n3" });
    expect(groupRuleFromResponse({ ...stored, operator: "exists", expectedValue: true })).toMatchObject({ operator: "exists", expectedValue: "" });
  });

  it("describes each operator compactly", () => {
    expect(describeRule({ attributeName: "level", operator: "eq", expectedValue: 3 })).toBe("level = 3");
    expect(describeRule({ attributeName: "department", operator: "eq", expectedValue: "Ops" })).toBe("department = \"Ops\"");
    expect(describeRule({ attributeName: "level", operator: "gte", expectedValue: 5 })).toBe("level ≥ 5");
    expect(describeRule({ attributeName: "level", operator: "in", expectedValue: [2, 3] })).toBe("level en [2, 3]");
    expect(describeRule({ attributeName: "department", operator: "contains", expectedValue: "eng" })).toBe("department contiene \"eng\"");
    expect(describeRule({ attributeName: "hiredOn", operator: "exists", expectedValue: true })).toBe("hiredOn tiene valor");
  });
});
