import { describe, expect, it } from "vitest";
import { groupDefaults, groupPayload, groupSchema } from "./group";

describe("group form", () => {
  it("normalizes optional values", () => {
    const values = groupSchema.parse({ ...groupDefaults(), name: " Engineering ", description: " " });
    expect(groupPayload(values)).toEqual({ name: "Engineering", description: null });
  });
});
