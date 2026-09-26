import { describe, expect, it } from "vitest";
import { parseSourceDocument, profileMappingCreatePayload, profileMappingSchema, profileMappingUpdatePayload } from "./profile-mapping";

const valid = {
  applicationSystemId: "11111111-1111-4111-8111-111111111111",
  sourcePath: " name.givenName ",
  targetAttributeDefinitionId: "66666666-6666-4666-8666-666666666666",
  isAuthoritative: true,
  isActive: false
};

describe("profile mapping form", () => {
  it("builds a SCIM-only create request with a trimmed path", () => {
    expect(profileMappingCreatePayload(valid)).toEqual({
      applicationSystemId: valid.applicationSystemId,
      sourceSystem: "SCIM",
      sourcePath: "name.givenName",
      targetAttributeDefinitionId: valid.targetAttributeDefinitionId,
      isAuthoritative: true
    });
  });

  it("carries the loaded version and active flag on update", () => {
    expect(profileMappingUpdatePayload(valid, 4)).toMatchObject({ sourceSystem: "SCIM", isActive: false, version: 4 });
  });

  it("accepts SCIM attribute paths and rejects anything else", () => {
    for (const path of ["name.givenName", "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department", "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User.department", "emails[type eq \"work\"].value", "$.department"])
      expect(profileMappingSchema.safeParse({ ...valid, sourcePath: path }).success).toBe(true);
    for (const path of ["name[0].given", "emails[type ne \"work\"].value", "name given", "a".repeat(301)])
      expect(profileMappingSchema.safeParse({ ...valid, sourcePath: path }).success).toBe(false);
  });

  it("only accepts JSON objects as simulation documents", () => {
    expect(parseSourceDocument("{\"name\":{\"givenName\":\"Grace\"}}")).toEqual({ ok: true, document: { name: { givenName: "Grace" } } });
    expect(parseSourceDocument("[1,2]").ok).toBe(false);
    expect(parseSourceDocument("{oops").ok).toBe(false);
  });
});
