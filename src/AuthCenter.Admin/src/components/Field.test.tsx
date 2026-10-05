import { render, screen } from "@testing-library/react";
import { Field } from "./Field";

describe("Field", () => {
  it("names the control by its label and describes it with the help and the error", () => {
    render(<Field label="Nombre" help="Ej.: CRM corporativo" error="Este campo es obligatorio."><input /></Field>);
    const input = screen.getByRole("textbox", { name: "Nombre" });
    expect(input).toHaveAccessibleDescription("Ej.: CRM corporativo Este campo es obligatorio.");
    expect(input).toHaveAttribute("aria-invalid", "true");
  });

  it("leaves a valid control without a description", () => {
    render(<Field label="Correo"><input /></Field>);
    const input = screen.getByRole("textbox", { name: "Correo" });
    expect(input).not.toHaveAttribute("aria-describedby");
    expect(input).not.toHaveAttribute("aria-invalid");
  });
});
