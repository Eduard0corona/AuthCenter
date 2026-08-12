import { describe, expect, it } from "vitest";
import { permissionPayload, permissionSchema } from "./permission";

describe("permission form", () => {
  it("normalizes create payload and rejects unsafe codes", () => {
    const values = permissionSchema.parse({ applicationSystemId: "11111111-1111-4111-8111-111111111111", code: "APP_USERS_READ", name: " Read users ", description: "" });
    expect(permissionPayload(values, true)).toMatchObject({ code: "APP_USERS_READ", name: "Read users", description: null });
    expect(permissionSchema.safeParse({ ...values, code: "app:read" }).success).toBe(false);
  });
});
