import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ReauthenticationDialog } from "./ReauthenticationDialog";

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

function renderDialog(onProof = vi.fn().mockResolvedValue(undefined)) {
  render(<ReauthenticationDialog open purpose="admin.federation.change" title="Guardar proveedor" detail="Cambia el inicio de sesión." confirmLabel="Verificar y guardar" onCancel={vi.fn()} onProof={onProof} />);
  return onProof;
}

describe("ReauthenticationDialog", () => {
  it("keeps a wrong password inside the dialog, clears it and lets the operator retry", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(json({ success: false, errorCode: "INVALID_REAUTHENTICATION", message: "Reauthentication failed." }, 400))
      .mockResolvedValueOnce(json({ success: true, data: { proofToken: "proof-1", assuranceLevel: "Password", expiresIn: 300 } }));
    const onProof = renderDialog();
    const password = screen.getByLabelText("Tu contraseña actual");

    await user.type(password, "mal escrita");
    await user.click(screen.getByRole("button", { name: "Verificar y guardar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("La contraseña no es correcta. Inténtalo de nuevo.");
    expect(password).toHaveValue("");
    expect(password).toHaveFocus();
    expect(password).toHaveAttribute("aria-invalid", "true");
    expect(onProof).not.toHaveBeenCalled();

    await user.type(password, "AdminSecret123");
    await user.click(screen.getByRole("button", { name: "Verificar y guardar" }));

    expect(onProof).toHaveBeenCalledWith("proof-1");
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("reads an older server's 401 for a wrong password the same way", async () => {
    const user = userEvent.setup();
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(json({ success: false, errorCode: "INVALID_REAUTHENTICATION", message: "Reauthentication failed." }, 401));
    renderDialog();

    await user.type(screen.getByLabelText("Tu contraseña actual"), "mal escrita");
    await user.click(screen.getByRole("button", { name: "Verificar y guardar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("La contraseña no es correcta.");
  });

  it("offers to sign in again when the session really ended", async () => {
    const user = userEvent.setup();
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(new Response(null, { status: 401 }));
    renderDialog();

    await user.type(screen.getByLabelText("Tu contraseña actual"), "AdminSecret123");
    await user.click(screen.getByRole("button", { name: "Verificar y guardar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Tu sesión terminó.");
    expect(screen.getByRole("button", { name: "Iniciar sesión de nuevo" })).toBeInTheDocument();
  });

  it("asks for the password before calling the server", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.spyOn(globalThis, "fetch");
    renderDialog();

    await user.click(screen.getByRole("button", { name: "Verificar y guardar" }));

    expect(screen.getByRole("alert")).toHaveTextContent("Escribe tu contraseña actual.");
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
