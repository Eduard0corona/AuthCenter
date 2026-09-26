import { ApiError } from "./client";
import { errorMessage, isStaleBuildError } from "./errors";

describe("errorMessage", () => {
  it("lists validation details without a trace reference", () => {
    const error = new ApiError(400, "VALIDATION_FAILED", "One or more validation errors occurred.", ["'Name' must not be empty."], "trace-1");

    expect(errorMessage(error)).toBe("Revisa los datos del formulario. 'Name' must not be empty.");
  });

  it("quotes the trace reference of server failures", () => {
    const error = new ApiError(500, "INTERNAL_ERROR", "An unexpected error occurred.", [], "4bf92f3577b34da6a3ce929d0e0e4736");

    expect(errorMessage(error)).toBe("AuthCenter no pudo completar la operación. Referencia: 4bf92f3577b34da6a3ce929d0e0e4736");
  });

  it("prefers the page's text for a known code", () => {
    const error = new ApiError(400, "EVENT_HOOK_VERIFICATION_FAILED", "Endpoint did not echo the verification challenge.");

    expect(errorMessage(error, { EVENT_HOOK_VERIFICATION_FAILED: "El endpoint no devolvió el reto." })).toBe("El endpoint no devolvió el reto.");
  });

  it("keeps domain messages and explains network failures", () => {
    expect(errorMessage(new ApiError(400, "LAST_SUPER_ADMIN", "The last SuperAdmin cannot be removed."))).toBe("The last SuperAdmin cannot be removed.");
    expect(errorMessage(new TypeError("Failed to fetch"))).toMatch(/No pudimos conectar/);
    expect(errorMessage("boom")).toBe("Ocurrió un error inesperado.");
  });

  it("names who and which roles a separation of duties conflict is about", () => {
    const error = new ApiError(409, "SOD_CONFLICT", "ana@example.com would hold both 'CRM:Compras' and 'CRM:Pagos'…", ["ana@example.com", "CRM:Compras", "CRM:Pagos", "Pagos"]);

    expect(errorMessage(error)).toBe("ana@example.com tendría a la vez los roles «CRM:Compras» y «CRM:Pagos», que la regla de segregación de funciones «Pagos» no permite.");
  });

  it("recognizes a lazy chunk from a previous deployment", () => {
    expect(isStaleBuildError(new TypeError("Failed to fetch dynamically imported module: /admin-v2/assets/UsersPage-abc.js"))).toBe(true);
    expect(isStaleBuildError(new Error("Cannot read properties of undefined"))).toBe(false);
    expect(errorMessage(new TypeError("Failed to fetch dynamically imported module: /admin-v2/assets/UsersPage-abc.js"))).toMatch(/Recarga la página/);
  });
});
