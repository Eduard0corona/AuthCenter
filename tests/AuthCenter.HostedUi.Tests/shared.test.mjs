import test from "node:test";
import assert from "node:assert/strict";
import { safeLocalPath } from "../../src/AuthCenter.Api/wwwroot/assets/shared.js";

const origin = "https://identity.example.test";

test("local paths are preserved with query and fragment", () => {
  assert.equal(safeLocalPath("/portal", origin, "/fallback"), "/portal");
  assert.equal(safeLocalPath("/admin-v2/users?page=2#top", origin, "/fallback"), "/admin-v2/users?page=2#top");
});

test("external, protocol-relative and backslash paths fall back", () => {
  for (const value of [
    "https://evil.example/",
    "//evil.example",
    "/\\evil.example",
    "/\\/evil.example",
    "\\\\evil.example",
    "/\tevil",
    "javascript:alert(1)",
    "",
    null,
    undefined
  ]) {
    assert.equal(safeLocalPath(value, origin, "/fallback"), "/fallback", String(value));
  }
});
