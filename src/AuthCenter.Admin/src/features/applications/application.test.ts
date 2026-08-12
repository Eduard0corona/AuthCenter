import { applicationDefaults, applicationPayload, applicationSchema } from "./application";

it("rejects origins and accepts a comma-separated domain allow-list", () => {
  const base = applicationDefaults();
  expect(applicationSchema.safeParse({ ...base, code: "SHOP", name: "Shop", allowedEmailDomains: "empresa.com, filial.mx" }).success).toBe(true);
  expect(applicationSchema.safeParse({ ...base, code: "SHOP", name: "Shop", allowedEmailDomains: "https://empresa.com" }).success).toBe(false);
});

it("normalizes a new application without inventing a default role", () => {
  const payload = applicationPayload({ ...applicationDefaults(), code: "shop_app", name: " Mi tienda ", description: "" });
  expect(payload).toEqual(expect.objectContaining({ code: "SHOP_APP", name: "Mi tienda", description: null, defaultRoleId: null }));
});
