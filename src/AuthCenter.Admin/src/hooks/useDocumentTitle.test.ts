import { renderHook } from "@testing-library/react";
import { CONSOLE_TITLE, documentTitle, useDocumentTitle } from "./useDocumentTitle";

describe("documentTitle", () => {
  it("names the page before the console", () => {
    expect(documentTitle("Clientes OAuth")).toBe("Clientes OAuth · Consola de administración");
  });

  it("falls back to the console's name", () => {
    expect(documentTitle("  ")).toBe(CONSOLE_TITLE);
    expect(documentTitle(null)).toBe(CONSOLE_TITLE);
  });
});

describe("useDocumentTitle", () => {
  it("sets the tab's title and follows changes", () => {
    const { rerender } = renderHook(({ page }: { page: string }) => useDocumentTitle(page), { initialProps: { page: "Usuarios" } });
    expect(document.title).toBe("Usuarios · Consola de administración");
    rerender({ page: "Grace Hopper" });
    expect(document.title).toBe("Grace Hopper · Consola de administración");
  });

  it("leaves the title alone without a page", () => {
    document.title = "Anterior";
    renderHook(() => useDocumentTitle(undefined));
    expect(document.title).toBe("Anterior");
  });
});
