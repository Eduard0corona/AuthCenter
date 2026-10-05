import test from "node:test";
import assert from "node:assert/strict";
import { errorMessage, safeLocalPath } from "../../src/AuthCenter.Api/wwwroot/assets/shared.js";

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

test("an error the page has no text for is explained in Spanish, never with the server's message", () => {
  const serverError = (status, code = "SOMETHING") => Object.assign(new Error("Validation failed for the request."), { status, code });
  assert.equal(errorMessage(serverError(429)), "Demasiados intentos. Espera un momento e inténtalo de nuevo.");
  assert.equal(errorMessage(serverError(401)), "Tu sesión terminó. Inicia sesión de nuevo.");
  assert.equal(errorMessage(serverError(503)), "Algo salió mal de nuestro lado. Inténtalo de nuevo en unos minutos.");
  assert.equal(errorMessage(serverError(400)), "No pudimos completar la operación. Revisa los datos e inténtalo de nuevo.");
  // A failed connection keeps the page's own message, which api() already wrote in Spanish.
  const offline = Object.assign(new Error("No pudimos conectar con el servidor."), { status: 0, code: "NETWORK_ERROR" });
  assert.equal(errorMessage(offline), "No pudimos conectar con el servidor.");
});
