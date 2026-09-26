import type { ProfileAttributeDefinition } from "../../api/types";
import { convertValue, describeConstraints, newProfileAttributeSchema, profileAttributeDefaults, profileAttributePayload, profileAttributeSchema } from "./profile-schema";

const definition: ProfileAttributeDefinition = {
  id: "66666666-6666-4666-8666-666666666666", key: "department", displayName: "Departamento", description: null, dataType: "String",
  isRequired: true, isActive: true, defaultValue: "Operaciones", minLength: 2, maxLength: 80, minimumNumber: null, maximumNumber: null,
  validationPattern: null, allowedValues: ["Operaciones", "Ingeniería"], createdAt: "2026-08-11T00:00:00Z", updatedAt: null
};

describe("profile schema helpers", () => {
  it("round-trips a definition through the form", () => {
    const values = profileAttributeDefaults(definition);

    expect(values.allowedValues).toBe("Operaciones\nIngeniería");
    expect(profileAttributePayload(values, false)).toEqual({
      displayName: "Departamento", description: null, dataType: "String", isRequired: true, defaultValue: "Operaciones",
      minLength: 2, maxLength: 80, minimumNumber: null, maximumNumber: null, validationPattern: null,
      allowedValues: ["Operaciones", "Ingeniería"], isActive: true
    });
  });

  it("types numbers, booleans and dates and drops constraints of other types", () => {
    const values = { ...profileAttributeDefaults(), key: "Level", displayName: "Nivel", dataType: "Integer" as const, minLength: "3", minimumNumber: "1", maximumNumber: "10", defaultValue: "5", allowedValues: "1\n5\n10" };

    expect(profileAttributePayload(values, true)).toMatchObject({ key: "level", defaultValue: 5, minLength: null, minimumNumber: 1, maximumNumber: 10, allowedValues: [1, 5, 10] });
    expect(convertValue("true", "Boolean")).toBe(true);
    expect(convertValue("2026-02-30x", "Date")).toBeUndefined();
    expect(convertValue("1.5", "Integer")).toBeUndefined();
  });

  it("requires a default for required attributes and rejects reserved keys", () => {
    const required = profileAttributeSchema.safeParse({ ...profileAttributeDefaults(), displayName: "Centro de costos", isRequired: true });
    expect(required.success).toBe(false);

    const reserved = newProfileAttributeSchema.safeParse({ ...profileAttributeDefaults(), key: "email", displayName: "Correo" });
    expect(reserved.success ? [] : reserved.error.issues.map((issue) => issue.path.join("."))).toContain("key");
  });

  it("summarizes constraints for the list", () => {
    expect(describeConstraints(definition)).toBe("2–80 caracteres · 2 valores permitidos");
  });
});
