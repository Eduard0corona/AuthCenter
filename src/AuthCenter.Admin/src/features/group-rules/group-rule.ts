import { z } from "zod";
import type { DynamicGroupRule, GroupRuleExpectedValue, ProfileAttributeDefinition } from "../../api/types";

/** The backend only evaluates equality today; the field stays explicit so new operators surface in the UI. */
export const groupRuleOperators = ["eq"] as const;

export const groupRuleSchema = z.object({
  directoryGroupId: z.string().uuid("Selecciona un grupo válido."),
  profileAttributeDefinitionId: z.string().uuid("Selecciona un atributo válido."),
  operator: z.enum(groupRuleOperators),
  expectedValue: z.string().trim().min(1, "Indica el valor esperado."),
  isActive: z.boolean()
});

export type GroupRuleFormValues = z.input<typeof groupRuleSchema>;

export function groupRuleDefaults(): GroupRuleFormValues {
  return { directoryGroupId: "", profileAttributeDefinitionId: "", operator: "eq", expectedValue: "", isActive: true };
}

export function groupRuleFromResponse(rule: DynamicGroupRule): GroupRuleFormValues {
  return {
    directoryGroupId: rule.directoryGroupId,
    profileAttributeDefinitionId: rule.profileAttributeDefinitionId,
    operator: groupRuleOperators.includes(rule.operator as typeof groupRuleOperators[number]) ? rule.operator as typeof groupRuleOperators[number] : "eq",
    expectedValue: expectedValueToInput(rule.expectedValue),
    isActive: rule.isActive
  };
}

export function expectedValueToInput(value: GroupRuleExpectedValue | null | undefined): string {
  if (value === null || value === undefined) return "";
  return String(value);
}

export type ExpectedValueConversion = { ok: true; value: GroupRuleExpectedValue } | { ok: false; error: string };

/**
 * Converts the operator's text into the JSON type the attribute stores. The backend matches the
 * raw JSON literally, so a "42" string would never equal an Integer attribute holding 42.
 */
export function convertExpectedValue(raw: string, dataType: ProfileAttributeDefinition["dataType"] | undefined): ExpectedValueConversion {
  const text = raw.trim();
  if (!text) return { ok: false, error: "Indica el valor esperado." };
  switch (dataType) {
    case "Integer": {
      if (!/^-?\d+$/.test(text)) return { ok: false, error: "El atributo es entero; usa solo dígitos." };
      const value = Number(text);
      if (!Number.isSafeInteger(value)) return { ok: false, error: "El entero excede el rango soportado." };
      return { ok: true, value };
    }
    case "Decimal": {
      const value = Number(text);
      if (!Number.isFinite(value)) return { ok: false, error: "El atributo es decimal; usa un número válido." };
      return { ok: true, value };
    }
    case "Boolean": {
      if (text === "true") return { ok: true, value: true };
      if (text === "false") return { ok: true, value: false };
      return { ok: false, error: "El atributo es booleano; usa true o false." };
    }
    case "Date":
    case "DateTime": {
      if (Number.isNaN(new Date(text).valueOf())) return { ok: false, error: "Usa una fecha ISO 8601 válida." };
      return { ok: true, value: text };
    }
    default:
      return { ok: true, value: text };
  }
}

export function groupRuleCreatePayload(values: GroupRuleFormValues, expectedValue: GroupRuleExpectedValue) {
  const parsed = groupRuleSchema.parse(values);
  return { directoryGroupId: parsed.directoryGroupId, profileAttributeDefinitionId: parsed.profileAttributeDefinitionId, operator: parsed.operator, expectedValue };
}

export function groupRuleUpdatePayload(values: GroupRuleFormValues, expectedValue: GroupRuleExpectedValue, version: number) {
  const parsed = groupRuleSchema.parse(values);
  return { profileAttributeDefinitionId: parsed.profileAttributeDefinitionId, operator: parsed.operator, expectedValue, isActive: parsed.isActive, version };
}

export function describeRule(rule: Pick<DynamicGroupRule, "attributeName" | "operator" | "expectedValue">): string {
  const operator = rule.operator === "eq" ? "=" : rule.operator;
  return `${rule.attributeName} ${operator} ${JSON.stringify(rule.expectedValue)}`;
}
