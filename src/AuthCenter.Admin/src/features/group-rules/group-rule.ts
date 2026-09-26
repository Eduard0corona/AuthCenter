import { z } from "zod";
import type { DynamicGroupRule, GroupRuleExpectedValue, GroupRuleScalar, ProfileAttributeDefinition } from "../../api/types";

type DataType = ProfileAttributeDefinition["dataType"];

/** The operators the backend evaluates (GroupRuleEvaluator); a group admits a user when any of its rules matches. */
export const groupRuleOperators = ["eq", "ne", "in", "contains", "startsWith", "gt", "gte", "lt", "lte", "exists"] as const;
export type GroupRuleOperator = typeof groupRuleOperators[number];

export const operatorLabels: Record<GroupRuleOperator, string> = {
  eq: "Es igual a",
  ne: "Es distinto de",
  in: "Es uno de",
  contains: "Contiene",
  startsWith: "Empieza por",
  gt: "Mayor que",
  gte: "Mayor o igual que",
  lt: "Menor que",
  lte: "Menor o igual que",
  exists: "Tiene un valor"
};

const operatorSymbols: Record<GroupRuleOperator, string> = {
  eq: "=", ne: "≠", in: "en", contains: "contiene", startsWith: "empieza por", gt: ">", gte: "≥", lt: "<", lte: "≤", exists: "tiene valor"
};

const orderedTypes: readonly DataType[] = ["Integer", "Decimal", "Date", "DateTime"];
const maxListValues = 100;

/** Same rule as the backend: text operators need text, ordering needs numbers or dates. */
export function operatorsFor(dataType: DataType | undefined): GroupRuleOperator[] {
  return groupRuleOperators.filter((operator) => {
    if (operator === "contains" || operator === "startsWith") return dataType === "String";
    if (operator === "gt" || operator === "gte" || operator === "lt" || operator === "lte") return dataType !== undefined && orderedTypes.includes(dataType);
    return true;
  });
}

export const groupRuleSchema = z.object({
  directoryGroupId: z.string().uuid("Selecciona un grupo válido."),
  profileAttributeDefinitionId: z.string().uuid("Selecciona un atributo válido."),
  operator: z.enum(groupRuleOperators),
  expectedValue: z.string().trim(),
  isActive: z.boolean()
}).superRefine((values, context) => {
  if (values.operator !== "exists" && !values.expectedValue)
    context.addIssue({ code: "custom", path: ["expectedValue"], message: values.operator === "in" ? "Indica al menos un valor." : "Indica el valor esperado." });
});

export type GroupRuleFormValues = z.input<typeof groupRuleSchema>;

export function groupRuleDefaults(): GroupRuleFormValues {
  return { directoryGroupId: "", profileAttributeDefinitionId: "", operator: "eq", expectedValue: "", isActive: true };
}

export function isGroupRuleOperator(value: string): value is GroupRuleOperator {
  return (groupRuleOperators as readonly string[]).includes(value);
}

export function groupRuleFromResponse(rule: DynamicGroupRule): GroupRuleFormValues {
  const operator = isGroupRuleOperator(rule.operator) ? rule.operator : "eq";
  return {
    directoryGroupId: rule.directoryGroupId,
    profileAttributeDefinitionId: rule.profileAttributeDefinitionId,
    operator,
    expectedValue: operator === "exists" ? "" : expectedValueToInput(rule.expectedValue),
    isActive: rule.isActive
  };
}

/** A list is edited one value per line. */
export function expectedValueToInput(value: GroupRuleExpectedValue | null | undefined): string {
  if (value === null || value === undefined) return "";
  return Array.isArray(value) ? value.map(String).join("\n") : String(value);
}

export type ExpectedValueConversion = { ok: true; value: GroupRuleExpectedValue } | { ok: false; error: string };
type ScalarConversion = { ok: true; value: GroupRuleScalar } | { ok: false; error: string };

/**
 * Converts the form's text into the JSON the operator expects for the attribute's type: one value,
 * a list (one per line) for "in", or true for "exists". A "42" string would never equal an Integer
 * attribute holding 42, so numbers and booleans are sent as such.
 */
export function convertExpectedValue(raw: string, dataType: DataType | undefined, operator: GroupRuleOperator = "eq"): ExpectedValueConversion {
  if (operator === "exists") return { ok: true, value: true };
  if (operator === "in") {
    const lines = [...new Set(raw.split("\n").map((line) => line.trim()).filter(Boolean))];
    if (lines.length === 0) return { ok: false, error: "Indica al menos un valor, uno por línea." };
    if (lines.length > maxListValues) return { ok: false, error: `Usa como máximo ${maxListValues} valores.` };
    const values: GroupRuleScalar[] = [];
    for (const line of lines) {
      const converted = convertScalar(line, dataType);
      if (!converted.ok) return { ok: false, error: `${line}: ${converted.error}` };
      values.push(converted.value);
    }
    return { ok: true, value: values };
  }
  return convertScalar(raw, dataType);
}

function convertScalar(raw: string, dataType: DataType | undefined): ScalarConversion {
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
    case "Date": {
      // The profile stores dates as yyyy-MM-dd, so that is the only form that can match.
      if (!/^\d{4}-\d{2}-\d{2}$/.test(text) || Number.isNaN(new Date(`${text}T00:00:00Z`).valueOf())) return { ok: false, error: "Usa una fecha con el formato AAAA-MM-DD." };
      return { ok: true, value: text };
    }
    case "DateTime": {
      if (!/(Z|[+-]\d{2}:\d{2})$/i.test(text) || Number.isNaN(new Date(text).valueOf())) return { ok: false, error: "Usa una fecha y hora ISO 8601 con zona, por ejemplo 2026-08-13T09:00:00Z." };
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
  if (!isGroupRuleOperator(rule.operator)) return `${rule.attributeName} ${rule.operator} ${JSON.stringify(rule.expectedValue)}`;
  const symbol = operatorSymbols[rule.operator];
  if (rule.operator === "exists") return `${rule.attributeName} ${symbol}`;
  const value = Array.isArray(rule.expectedValue) ? `[${rule.expectedValue.map((item) => JSON.stringify(item)).join(", ")}]` : JSON.stringify(rule.expectedValue);
  return `${rule.attributeName} ${symbol} ${value}`;
}
