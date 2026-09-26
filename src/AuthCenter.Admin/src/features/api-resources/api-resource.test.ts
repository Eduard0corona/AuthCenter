import type { ApiResource } from "../../api/types";
import { apiResourceDefaults, apiResourceSchema, createApiResourcePayload, isResourceIndicator, newApiResourceSchema, removedScopes, updateApiResourcePayload } from "./api-resource";

const resource: ApiResource = {
  id: "abababab-abab-4bab-8bab-abababababab", applicationSystemId: "11111111-1111-4111-8111-111111111111", applicationCode: "TIENDITAPP", applicationName: "TienditApp",
  identifier: "https://api.tiendit.app/orders", displayName: "Orders API", description: null, isActive: true,
  scopes: [{ id: "1", name: "orders.read", displayName: "Leer pedidos", description: null }, { id: "2", name: "orders.write", displayName: "Modificar pedidos", description: "Crear y cancelar" }],
  createdAt: "2026-09-01T00:00:00Z", updatedAt: null
};

describe("API resource helpers", () => {
  it("accepts RFC 8707 resource indicators only", () => {
    expect(isResourceIndicator("https://api.example.com/orders")).toBe(true);
    expect(isResourceIndicator("urn:example:orders")).toBe(true);
    expect(isResourceIndicator("http://api.example.com")).toBe(false);
    expect(isResourceIndicator("https://api.example.com/#fragment")).toBe(false);
  });

  it("builds the create and update bodies", () => {
    const values = apiResourceDefaults(resource);

    expect(createApiResourcePayload(values)).toEqual({
      applicationSystemId: resource.applicationSystemId, identifier: resource.identifier, displayName: "Orders API", description: null,
      scopes: [{ name: "orders.read", displayName: "Leer pedidos", description: null }, { name: "orders.write", displayName: "Modificar pedidos", description: "Crear y cancelar" }]
    });
    expect(updateApiResourcePayload({ ...values, isActive: false })).toMatchObject({ displayName: "Orders API", isActive: false });
  });

  it("rejects duplicated, reserved and malformed scopes", () => {
    const base = apiResourceDefaults(resource);
    const duplicated = apiResourceSchema.safeParse({ ...base, scopes: [...base.scopes, { name: "orders.read", displayName: "Otra vez", description: "" }] });
    const reserved = apiResourceSchema.safeParse({ ...base, scopes: [{ name: "openid", displayName: "OpenID", description: "" }] });
    const missingApplication = newApiResourceSchema.safeParse({ ...base, applicationSystemId: "" });

    expect(duplicated.success).toBe(false);
    expect(reserved.success).toBe(false);
    expect(missingApplication.success).toBe(false);
  });

  it("lists the scopes an update removes", () => {
    const values = apiResourceDefaults(resource);
    expect(removedScopes(resource, { ...values, scopes: values.scopes.slice(0, 1) })).toEqual(["orders.write"]);
  });
});
