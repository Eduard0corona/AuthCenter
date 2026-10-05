import { askForPermission, needPermission, PERMISSION_LABELS, permissionLabel } from "./permissions";

describe("permission names", () => {
  it("names every AuthCenter permission in Spanish", () => {
    const codes = Object.keys(PERMISSION_LABELS);
    expect(codes).toHaveLength(27);
    expect(codes.every((code) => code.startsWith("AUTHCENTER_"))).toBe(true);
    expect(permissionLabel("AUTHCENTER_GOVERNANCE_WRITE")).toBe("Editar gobierno de accesos");
  });

  it("keeps the name of another application's permission, or its code", () => {
    expect(permissionLabel("TIENDIT_ORDERS_READ", "Consultar pedidos")).toBe("Consultar pedidos");
    expect(permissionLabel("AUTHCENTER_USERS_READ", "Read users")).toBe("Consultar usuarios");
    expect(permissionLabel("TIENDIT_ORDERS_READ", " ")).toBe("TIENDIT_ORDERS_READ");
    expect(permissionLabel("TIENDIT_ORDERS_READ")).toBe("TIENDIT_ORDERS_READ");
  });

  it("writes the sentences that ask for a permission", () => {
    expect(needPermission("AUTHCENTER_APPLICATIONS_READ", "elegir la aplicación")).toBe("Necesitas el permiso «Consultar aplicaciones» para elegir la aplicación.");
    expect(askForPermission("AUTHCENTER_APPLICATIONS_WRITE", "cambiar esta configuración")).toBe("Para cambiar esta configuración, pide a un administrador el permiso «Editar aplicaciones».");
  });
});
