import { describeHolding, reviewDefaults, reviewPayload, reviewSchema, sodRulePayload, sodRuleSchema, toLocalInput } from "./governance";

describe("governance helpers", () => {
  it("describes how a role is held", () => {
    expect(describeHolding({ roleId: "r", roleName: "Pagos", applicationCode: "ERP", direct: true, groups: ["Tesorería"] })).toBe("ERP · Pagos (directo; grupos: Tesorería)");
    expect(describeHolding({ roleId: "r", roleName: "Pagos", applicationCode: null, direct: false, groups: [] })).toBe("Pagos (sin origen)");
  });

  it("validates the review's due date and builds its payload", () => {
    const defaults = reviewDefaults(new Date(2026, 8, 26, 10, 30));
    expect(defaults.dueAt).toBe("2026-10-10T10:30");
    const soon = { ...defaults, name: "Trimestral", applicationSystemId: "app", dueAt: toLocalInput(new Date(Date.now() + 10 * 60 * 1000)) };
    expect(reviewSchema.safeParse(soon).success).toBe(false);
    const later = { ...soon, dueAt: toLocalInput(new Date(Date.now() + 3 * 24 * 60 * 60 * 1000)), recurrenceMonths: "3" };
    expect(reviewSchema.safeParse(later).success).toBe(true);
    expect(reviewPayload(later)).toMatchObject({ name: "Trimestral", applicationSystemId: "app", recurrenceMonths: 3, revokeUnreviewed: false });
    expect(reviewPayload({ ...later, recurrenceMonths: "" }).recurrenceMonths).toBeNull();
  });

  it("requires two different roles for a separation of duties rule", () => {
    const values = { name: "Pagos", description: "", firstRoleId: "a", secondRoleId: "a", isActive: true };
    expect(sodRuleSchema.safeParse(values).success).toBe(false);
    expect(sodRuleSchema.safeParse({ ...values, secondRoleId: "b" }).success).toBe(true);
    expect(sodRulePayload({ ...values, secondRoleId: "b" }, 4)).toEqual({ name: "Pagos", description: null, firstRoleId: "a", secondRoleId: "b", isActive: true, version: 4 });
    expect(sodRulePayload({ ...values, secondRoleId: "b" })).not.toHaveProperty("version");
  });
});
