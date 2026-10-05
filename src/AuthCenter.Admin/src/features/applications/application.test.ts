import type { ApplicationSummary } from "../../api/types";
import { applicationDefaults, applicationPayload, applicationSchema, audienceOf, registrationForAudience } from "./application";

describe("audience", () => {
  it("starts a new application for employees, with closed registration", () => {
    expect(applicationDefaults()).toMatchObject({ audience: "Employees", registrationMode: "Closed" });
    expect(applicationPayload({ ...applicationDefaults(), code: "shop", name: "Shop" })).toMatchObject({ audience: "Employees" });
  });

  it("opens registration with email confirmation for consumers and closes it for employees", () => {
    expect(registrationForAudience("Consumers")).toEqual({ registrationMode: "Open", requireEmailConfirmation: true });
    expect(registrationForAudience("Employees")).toEqual({ registrationMode: "Closed" });
  });

  it("keeps an application's audience and sends it back on update", () => {
    const application = { id: "a", code: "SHOP", name: "Shop", description: null, isActive: true, createdAt: "2026-08-11T00:00:00Z", updatedAt: null, version: 2, branding: null,
      registrationSettings: { ...applicationDefaults(), audience: "Consumers", registrationMode: "InviteOnly", allowedEmailDomains: null, defaultRoleId: null } } as ApplicationSummary;
    const values = applicationDefaults(application);
    expect(values.audience).toBe("Consumers");
    expect(applicationPayload(values, application)).toMatchObject({ audience: "Consumers", registrationMode: "InviteOnly", version: 2 });
  });

  it("reads a response without an audience the way the server does", () => {
    expect(audienceOf({ registrationMode: "Open" })).toBe("Consumers");
    expect(audienceOf({ registrationMode: "ApprovalRequired" })).toBe("Employees");
  });
});

it("rejects origins and accepts a comma-separated domain allow-list", () => {
  const base = applicationDefaults();
  expect(applicationSchema.safeParse({ ...base, code: "SHOP", name: "Shop", allowedEmailDomains: "empresa.com, filial.mx" }).success).toBe(true);
  expect(applicationSchema.safeParse({ ...base, code: "SHOP", name: "Shop", allowedEmailDomains: "https://empresa.com" }).success).toBe(false);
});

it("normalizes a new application without inventing a default role", () => {
  const payload = applicationPayload({ ...applicationDefaults(), code: "shop_app", name: " Mi tienda ", description: "" });
  expect(payload).toEqual(expect.objectContaining({ code: "SHOP_APP", name: "Mi tienda", description: null, defaultRoleId: null }));
});
