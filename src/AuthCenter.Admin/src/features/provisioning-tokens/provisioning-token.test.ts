import { describe, expect, it, vi } from "vitest";
import { provisioningTokenPayload, provisioningTokenSchema } from "./provisioning-token";

describe("provisioning token form", () => {
  it("builds a bounded scoped request", () => {
    vi.setSystemTime(new Date("2026-08-13T12:00:00Z"));
    const payload = provisioningTokenPayload({
      applicationSystemId: "11111111-1111-4111-8111-111111111111",
      name: "Directory sync",
      scopes: ["scim.users.read", "scim.groups.write"],
      expiresAt: "2026-11-13T12:00"
    });
    expect(payload).toMatchObject({ name: "Directory sync", scopes: ["scim.users.read", "scim.groups.write"] });
    expect(payload.expiresAt).toMatch(/^2026-11-13T/);
    vi.useRealTimers();
  });

  it("rejects unsupported scopes and expirations beyond one year", () => {
    vi.setSystemTime(new Date("2026-08-13T12:00:00Z"));
    expect(provisioningTokenSchema.safeParse({
      applicationSystemId: "11111111-1111-4111-8111-111111111111",
      name: "Directory sync",
      scopes: ["admin.full_access"],
      expiresAt: "2028-01-01T00:00"
    }).success).toBe(false);
    vi.useRealTimers();
  });
});
