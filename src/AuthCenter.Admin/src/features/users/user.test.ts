import { describe, expect, it } from "vitest";
import { userIdentityPayload, userIdentitySchema } from "./user";

describe("user identity form", () => {
  it("requires an HTTPS picture and normalizes optional values", () => {
    expect(userIdentitySchema.safeParse({ fullName: "Ada", pictureUrl: "http://example.test/ada.png" }).success).toBe(false);
    expect(userIdentityPayload({ fullName: " Ada Lovelace ", pictureUrl: "" })).toEqual({ fullName: "Ada Lovelace", pictureUrl: null });
  });
});
