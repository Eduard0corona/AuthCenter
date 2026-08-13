import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { SecretRevealDialog } from "./SecretRevealDialog";

describe("SecretRevealDialog", () => {
  beforeEach(() => {
    HTMLDialogElement.prototype.showModal = vi.fn(function (this: HTMLDialogElement) { this.setAttribute("open", ""); });
    HTMLDialogElement.prototype.close = vi.fn(function (this: HTMLDialogElement) { this.removeAttribute("open"); });
    Object.assign(navigator, { clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
  });

  it("warns, copies and requires an explicit close", async () => {
    const onClose = vi.fn();
    render(<SecretRevealDialog open secret="one-time-secret" title="Secreto nuevo" onClose={onClose} />);
    expect(screen.getByText("one-time-secret")).toBeInTheDocument();
    expect(screen.getByText(/no podrá volver a mostrar/i)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Copiar secreto" }));
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith("one-time-secret");
    await userEvent.click(screen.getByRole("button", { name: "Ya guardé el secreto" }));
    expect(onClose).toHaveBeenCalledOnce();
  });
});
