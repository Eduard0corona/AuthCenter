import { brandingSchema, contrastRatio } from "./branding";

describe("branding validation", () => {
  it("calculates WCAG contrast ratios", () => {
    expect(contrastRatio("#000000", "#ffffff")).toBeCloseTo(21, 2);
    expect(contrastRatio("#ffffff", "#ffffff")).toBeCloseTo(1, 2);
  });

  it("accepts complete HTTPS branding and rejects insecure links", () => {
    const valid = brandingSchema.safeParse({
      displayName: "Tienda",
      primaryColor: "#175cd3",
      backgroundColor: "#ffffff",
      logoUrl: "https://cdn.example.test/logo.svg",
      supportUrl: "https://example.test/support",
      privacyUrl: "https://example.test/privacy",
      termsUrl: "https://example.test/terms"
    });
    const invalid = brandingSchema.safeParse({
      displayName: "Tienda",
      primaryColor: "#ffffff",
      backgroundColor: "#ffffff",
      logoUrl: "http://example.test/logo.svg",
      supportUrl: "",
      privacyUrl: "",
      termsUrl: ""
    });

    expect(valid.success).toBe(true);
    expect(invalid.success).toBe(false);
  });
});
