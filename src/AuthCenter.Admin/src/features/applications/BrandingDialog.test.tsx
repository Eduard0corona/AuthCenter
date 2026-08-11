import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ApplicationSummary } from "../../api/types";
import { BrandingDialog } from "./BrandingDialog";

const application: ApplicationSummary = {
  id: "00000000-0000-0000-0000-000000000001",
  code: "SHOP",
  name: "Shop",
  description: null,
  isActive: true,
  createdAt: "2026-08-11T00:00:00Z",
  updatedAt: null,
  branding: {
    applicationCode: "SHOP",
    displayName: "Mi tienda",
    primaryColor: "#175CD3",
    backgroundColor: "#FFFFFF",
    logoUrl: "https://example.test/logo.svg",
    supportUrl: "https://example.test/support",
    privacyUrl: "https://example.test/privacy",
    termsUrl: "https://example.test/terms"
  }
};

it("submits every existing branding field without deleting privacy or terms", async () => {
  const user = userEvent.setup();
  const onSave = vi.fn().mockResolvedValue(undefined);
  render(<BrandingDialog application={application} busy={false} error="" onSave={onSave} onClose={vi.fn()} />);

  await user.click(screen.getByRole("button", { name: "Guardar branding" }));

  expect(onSave.mock.calls[0]?.[0]).toEqual(expect.objectContaining({
    displayName: "Mi tienda",
    privacyUrl: "https://example.test/privacy",
    termsUrl: "https://example.test/terms",
    supportUrl: "https://example.test/support"
  }));
});
