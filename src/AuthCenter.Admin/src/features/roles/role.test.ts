import { describe, expect, it } from "vitest";
import { rolePayload, roleSchema } from "./role";

describe("role form", () => {
  it("only creates non-system roles scoped to an application", () => {
    const values = roleSchema.parse({ name: " Operator ", description: "", applicationSystemId: "11111111-1111-4111-8111-111111111111" });
    expect(rolePayload(values)).toEqual({ name: "Operator", description: null, applicationSystemId: values.applicationSystemId, isSystemRole: false });
  });
});
