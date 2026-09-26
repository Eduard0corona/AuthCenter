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

  it("reads the trace id from the response body", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify({ success: false, errorCode: "INTERNAL_ERROR", message: "Boom", traceId: "body-trace" }), {
      status: 500,
      headers: { "content-type": "application/json" }
    }));

    const error = await apiRequest("/api/private").catch((value: unknown) => value);

    expect(error).toMatchObject({ status: 500, kind: "transient", traceId: "body-trace" });
  });

  it("renews an expired CSRF token once and repeats the request", async () => {
    setCsrfToken("stale-token");
    const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
    const fetchMock = vi.spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(json({ success: false, errorCode: "INVALID_CSRF_TOKEN", message: "A valid same-origin CSRF token is required." }, 400))
      .mockResolvedValueOnce(json({ success: true, data: { csrfToken: "fresh-token" } }))
      .mockResolvedValueOnce(json({ success: true, data: { saved: true } }));

    const result = await apiRequest<{ saved: boolean }>("/api/example", { method: "PUT", body: "{}" });

    expect(result).toEqual({ saved: true });
    expect(fetchMock.mock.calls[1]?.[0]).toBe("/ui-api/session");
    expect(new Headers(fetchMock.mock.calls[2]?.[1]?.headers).get("X-AuthCenter-CSRF")).toBe("fresh-token");
    expect(getCsrfTokenForTests()).toBe("fresh-token");
  });

  it("gives up after one CSRF renewal", async () => {
    setCsrfToken("stale-token");
    const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
    const fetchMock = vi.spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(json({ success: false, errorCode: "INVALID_CSRF_TOKEN", message: "Invalid" }, 400))
      .mockResolvedValueOnce(json({ success: true, data: { csrfToken: "fresh-token" } }))
      .mockResolvedValueOnce(json({ success: false, errorCode: "INVALID_CSRF_TOKEN", message: "Invalid" }, 400));

    const error = await apiRequest("/api/example", { method: "DELETE" }).catch((value: unknown) => value);

    expect(error).toMatchObject({ status: 400, code: "INVALID_CSRF_TOKEN" });
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });
});

