import { apiRequest, ApiError, getCsrfTokenForTests, setCsrfToken } from "./client";

describe("apiRequest", () => {
  it("keeps CSRF in memory and adds it only to unsafe requests", async () => {
    setCsrfToken("csrf-test-value");
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify({ success: true, data: { saved: true } }), {
      status: 200,
      headers: { "content-type": "application/json" }
    }));

    await apiRequest<{ saved: boolean }>("/api/example", { method: "POST", body: "{}" });

    const request = fetchMock.mock.calls[0];
    const init = request?.[1];
    expect(getCsrfTokenForTests()).toBe("csrf-test-value");
    expect(new Headers(init?.headers).get("X-AuthCenter-CSRF")).toBe("csrf-test-value");
    expect(init?.credentials).toBe("same-origin");
  });

  it("classifies forbidden responses without exposing server bodies", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify({ success: false, errorCode: "FORBIDDEN", message: "No permitido" }), {
      status: 403,
      headers: { "content-type": "application/json", "x-trace-id": "trace-1" }
    }));

    const error = await apiRequest("/api/private").catch((value: unknown) => value);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 403, code: "FORBIDDEN", kind: "authorization", traceId: "trace-1" });
  });
});
