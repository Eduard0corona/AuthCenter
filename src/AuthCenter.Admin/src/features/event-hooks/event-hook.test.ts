import { createEventHookPayload, describeSubscription, filterEventTypeGroups, groupEventTypes, normalizeTypes, updateEventHookPayload } from "./event-hook";

const catalog = [
  { type: "LOGIN_SUCCESS", category: "authentication" },
  { type: "LOGIN_FAILED", category: "authentication" },
  { type: "USER_CREATED", category: "users" },
  { type: "SOMETHING_NEW", category: "future-area" }
];

describe("event hook helpers", () => {
  it("groups the catalog by area with Spanish labels", () => {
    const groups = groupEventTypes(catalog);

    expect(groups.map((group) => group.label)).toEqual(["Autenticación", "Usuarios", "future-area"]);
    expect(groups[0]?.types).toEqual(["LOGIN_SUCCESS", "LOGIN_FAILED"]);
  });

  it("filters types and areas by text", () => {
    const groups = groupEventTypes(catalog);

    expect(filterEventTypeGroups(groups, "failed").map((group) => group.types)).toEqual([["LOGIN_FAILED"]]);
    expect(filterEventTypeGroups(groups, "usuarios")[0]?.types).toEqual(["USER_CREATED"]);
  });

  it("keeps only the wildcard when subscribing to everything", () => {
    expect(normalizeTypes(["LOGIN_FAILED", "*", "LOGIN_FAILED"])).toEqual(["*"]);
    expect(describeSubscription(["*"])).toBe("Todos los eventos");
    expect(describeSubscription(["LOGIN_FAILED", "USER_CREATED"])).toBe("2 tipos de evento");
  });

  it("requires a public HTTPS endpoint and builds the API payloads", () => {
    const values = { name: " SIEM ", url: "https://siem.example.com/hooks", applicationSystemId: "", eventTypes: ["LOGIN_FAILED"], isActive: true };

    expect(createEventHookPayload(values)).toEqual({ name: "SIEM", url: "https://siem.example.com/hooks", applicationSystemId: null, eventTypes: ["LOGIN_FAILED"] });
    expect(updateEventHookPayload({ ...values, isActive: false }, 7)).toEqual({ name: "SIEM", url: "https://siem.example.com/hooks", eventTypes: ["LOGIN_FAILED"], isActive: false, version: 7 });
    expect(() => createEventHookPayload({ ...values, url: "http://siem.example.com/hooks" })).toThrow();
    expect(() => createEventHookPayload({ ...values, eventTypes: [] })).toThrow();
  });
});
