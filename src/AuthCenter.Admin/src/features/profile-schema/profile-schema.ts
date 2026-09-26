import { z } from "zod";
import type { ProfileAttributeDefinition } from "../../api/types";

export type ProfileDataType = ProfileAttributeDefinition["dataType"];

export const DATA_TYPES: Array<{ value: ProfileDataType; label: string }> = [
  { value: "String", label: "Texto" },
  { value: "Integer", label: "Número entero" },
  { value: "Decimal", label: "Número decimal" },
  { value: "Boolean", label: "Sí / No" },
  { value: "Date", label: "Fecha" },
  { value: "DateTime", label: "Fecha y hora" }
];

/** Built-in profile fields a custom attribute cannot shadow (mirrors the server). */
const RESERVED_KEYS = new Set(["id", "email", "username", "fullname", "pictureurl", "isactive", "createdat", "updatedat"]);

const optionalInteger = z.string().trim().refine((value) => value === "" || /^\d+$/.test(value), "Usa un número entero no negativo.");
const optionalNumber = z.string().trim().refine((value) => value === "" || Number.isFinite(Number(value)), "Usa un número válido.");

export const profileAttributeSchema = z.object({
  key: z.string().trim(),
  displayName: z.string().trim().min(1, "El nombre visible es obligatorio.").max(200, "Usa máximo 200 caracteres."),
  description: z.string().trim().max(1000, "Usa máximo 1000 caracteres."),
  dataType: z.enum(["String", "Integer", "Decimal", "Boolean", "Date", "DateTime"]),
  isRequired: z.boolean(),
  isActive: z.boolean(),
  defaultValue: z.string(),
  minLength: optionalInteger,
  maxLength: optionalInteger,
  minimumNumber: optionalNumber,
  maximumNumber: optionalNumber,
  validationPattern: z.string().max(500, "Usa máximo 500 caracteres."),
  allowedValues: z.string()
}).superRefine((values, context) => {
  const issue = (path: string, message: string) => context.addIssue({ code: "custom", path: [path], message });
  if (values.minLength !== "" && values.maxLength !== "" && Number(values.minLength) > Number(values.maxLength)) issue("maxLength", "El máximo no puede ser menor que el mínimo.");
  if (values.maxLength !== "" && Number(values.maxLength) < 1) issue("maxLength", "El máximo debe ser al menos 1.");
  if (values.minimumNumber !== "" && values.maximumNumber !== "" && Number(values.minimumNumber) > Number(values.maximumNumber)) issue("maximumNumber", "El máximo no puede ser menor que el mínimo.");
  if (values.validationPattern.trim()) {
    try { new RegExp(values.validationPattern); } catch { issue("validationPattern", "La expresión regular no es válida."); }
  }
  const allowed = splitAllowedValues(values.allowedValues);
  if (allowed.length > 100) issue("allowedValues", "Define como máximo 100 valores permitidos.");
  for (const value of allowed) {
    if (convertValue(value, values.dataType) === undefined) { issue("allowedValues", `"${value}" no es un valor válido para el tipo ${dataTypeLabel(values.dataType)}.`); break; }
  }
  if (values.defaultValue.trim() && convertValue(values.defaultValue, values.dataType) === undefined) issue("defaultValue", `El valor no es válido para el tipo ${dataTypeLabel(values.dataType)}.`);
  if (values.isRequired && !values.defaultValue.trim()) issue("defaultValue", "Un atributo obligatorio necesita un valor predeterminado para que los perfiles existentes sigan siendo válidos.");
});

export const newProfileAttributeSchema = profileAttributeSchema.superRefine((values, context) => {
  const key = values.key.toLowerCase();
  if (!/^[a-z][a-z0-9_.-]{1,99}$/.test(key)) context.addIssue({ code: "custom", path: ["key"], message: "Usa de 2 a 100 caracteres en minúsculas: letras, números, punto, guion o guion bajo, empezando con letra." });
  else if (RESERVED_KEYS.has(key)) context.addIssue({ code: "custom", path: ["key"], message: "Esa clave corresponde a un campo integrado del perfil." });
});

export type ProfileAttributeFormValues = z.input<typeof profileAttributeSchema>;

export function dataTypeLabel(dataType: ProfileDataType): string {
  return DATA_TYPES.find((item) => item.value === dataType)?.label ?? dataType;
}

export function profileAttributeDefaults(definition?: ProfileAttributeDefinition): ProfileAttributeFormValues {
  if (!definition) {
    return { key: "", displayName: "", description: "", dataType: "String", isRequired: false, isActive: true, defaultValue: "", minLength: "", maxLength: "", minimumNumber: "", maximumNumber: "", validationPattern: "", allowedValues: "" };
  }
  return {
    key: definition.key,
    displayName: definition.displayName,
    description: definition.description ?? "",
    dataType: definition.dataType,
    isRequired: definition.isRequired,
    isActive: definition.isActive,
    defaultValue: definition.defaultValue === null ? "" : formatValue(definition.defaultValue, definition.dataType),
    minLength: definition.minLength?.toString() ?? "",
    maxLength: definition.maxLength?.toString() ?? "",
    minimumNumber: definition.minimumNumber?.toString() ?? "",
    maximumNumber: definition.maximumNumber?.toString() ?? "",
    validationPattern: definition.validationPattern ?? "",
    allowedValues: definition.allowedValues.map((value) => formatValue(value, definition.dataType)).join("\n")
  };
}

/** The request body; constraints that do not apply to the data type are dropped. */
export function profileAttributePayload(values: ProfileAttributeFormValues, create: boolean) {
  const parsed = (create ? newProfileAttributeSchema : profileAttributeSchema).parse(values);
  const isText = parsed.dataType === "String";
  const isNumber = parsed.dataType === "Integer" || parsed.dataType === "Decimal";
  const body = {
    displayName: parsed.displayName,
    description: parsed.description || null,
    dataType: parsed.dataType,
    isRequired: parsed.isRequired,
    defaultValue: parsed.defaultValue.trim() ? convertValue(parsed.defaultValue, parsed.dataType) ?? null : null,
    minLength: isText && parsed.minLength ? Number(parsed.minLength) : null,
    maxLength: isText && parsed.maxLength ? Number(parsed.maxLength) : null,
    minimumNumber: isNumber && parsed.minimumNumber ? Number(parsed.minimumNumber) : null,
    maximumNumber: isNumber && parsed.maximumNumber ? Number(parsed.maximumNumber) : null,
    validationPattern: isText && parsed.validationPattern.trim() ? parsed.validationPattern.trim() : null,
    allowedValues: parsed.dataType === "Boolean" ? [] : splitAllowedValues(parsed.allowedValues).map((value) => convertValue(value, parsed.dataType))
  };
  return create ? { key: parsed.key.toLowerCase(), ...body } : { ...body, isActive: parsed.isActive };
}

export function splitAllowedValues(text: string): string[] {
  return [...new Set(text.split(/\r?\n/).map((value) => value.trim()).filter(Boolean))];
}

/** The JSON value the server expects for the type, or undefined when the text is not one. */
export function convertValue(text: string, dataType: ProfileDataType): string | number | boolean | undefined {
  const value = text.trim();
  switch (dataType) {
    case "String": return text;
    case "Integer": return /^-?\d+$/.test(value) && Number.isSafeInteger(Number(value)) ? Number(value) : undefined;
    case "Decimal": return value !== "" && Number.isFinite(Number(value)) ? Number(value) : undefined;
    case "Boolean": return value === "true" ? true : value === "false" ? false : undefined;
    case "Date": return /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(new Date(`${value}T00:00:00Z`).valueOf()) ? value : undefined;
    case "DateTime": {
      // A local date and time from the form, or an ISO value with an offset.
      const date = new Date(value);
      return value && !Number.isNaN(date.valueOf()) ? date.toISOString() : undefined;
    }
  }
}

export function formatValue(value: string | number | boolean, dataType: ProfileDataType): string {
  if (dataType === "DateTime" && typeof value === "string") {
    const date = new Date(value);
    if (Number.isNaN(date.valueOf())) return value;
    const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
    return local.toISOString().slice(0, 16);
  }
  return String(value);
}

/** One line describing the constraints, for the list. */
export function describeConstraints(definition: ProfileAttributeDefinition): string {
  const parts: string[] = [];
  if (definition.minLength !== null || definition.maxLength !== null) parts.push(`${definition.minLength ?? 0}–${definition.maxLength ?? "∞"} caracteres`);
  if (definition.minimumNumber !== null || definition.maximumNumber !== null) parts.push(`entre ${definition.minimumNumber ?? "−∞"} y ${definition.maximumNumber ?? "∞"}`);
  if (definition.validationPattern) parts.push("patrón");
  if (definition.allowedValues.length > 0) parts.push(`${definition.allowedValues.length} valores permitidos`);
  return parts.join(" · ") || "Sin restricciones";
}
