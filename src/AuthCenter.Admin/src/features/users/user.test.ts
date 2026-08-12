import { describe, expect, it } from "vitest";
import { userIdentityPayload, userIdentitySchema } from "./user";
import { generateTemporaryPassword, userProvisioningDefaults, userProvisioningPayload, userProvisioningSchema } from "./provisioning";

describe("user identity form", () => {
  it("requires an HTTPS picture and normalizes optional values", () => {
    expect(userIdentitySchema.safeParse({ fullName: "Ada", pictureUrl: "http://example.test/ada.png" }).success).toBe(false);
    expect(userIdentityPayload({ fullName: " Ada Lovelace ", pictureUrl: "" })).toEqual({ fullName: "Ada Lovelace", pictureUrl: null });
  });
});

describe("user provisioning", () => {
  it("generates a strong temporary password without persistent state", () => {
    const password = generateTemporaryPassword(new Uint8Array(Array.from({ length: 40 }, (_, index) => index + 1)));
    expect(password).toHaveLength(20);
    expect(password).toMatch(/[A-Z]/);
    expect(password).toMatch(/[a-z]/);
    expect(password).toMatch(/[0-9]/);
  });

  it("requires an application for invitations and marks created passwords as temporary", () => {
    const invite = userProvisioningDefaults("invite");
    expect(userProvisioningSchema("invite").safeParse({ ...invite, fullName: "Ada", email: "ada@example.test" }).success).toBe(false);
    const create = { ...userProvisioningDefaults("create"), fullName: "Ada", email: "ADA@example.test", password: "Temporary12345" };
    expect(userProvisioningPayload("create", create)).toMatchObject({ email: "ada@example.test", isTemporaryPassword: true, grantApplicationAccess: false, roleIds: [] });
  });
});
