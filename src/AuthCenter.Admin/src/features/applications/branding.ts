import { z } from "zod";

const optionalHttpsUrl = z.string().trim().refine((value) => {
  if (!value) return true;
  try { return new URL(value).protocol === "https:"; } catch { return false; }
}, "Debe ser una URL HTTPS válida.");

export const brandingSchema = z.object({
  displayName: z.string().trim().min(1, "El nombre es obligatorio.").max(100),
  primaryColor: z.string().regex(/^#[0-9a-f]{6}$/i, "Usa un color hexadecimal."),
  backgroundColor: z.string().regex(/^#[0-9a-f]{6}$/i, "Usa un color hexadecimal."),
  logoUrl: optionalHttpsUrl,
  supportUrl: optionalHttpsUrl,
  privacyUrl: optionalHttpsUrl,
  termsUrl: optionalHttpsUrl
}).superRefine((value, context) => {
  if (contrastRatio(value.primaryColor, value.backgroundColor) < 4.5) {
    context.addIssue({ code: "custom", path: ["primaryColor"], message: "El color principal necesita contraste AA (4.5:1) sobre el fondo." });
  }
});

export type BrandingFormValues = z.infer<typeof brandingSchema>;

export function contrastRatio(first: string, second: string): number {
  const firstLuminance = luminance(first);
  const secondLuminance = luminance(second);
  return (Math.max(firstLuminance, secondLuminance) + 0.05) / (Math.min(firstLuminance, secondLuminance) + 0.05);
}

function luminance(hex: string): number {
  const channels = [hex.slice(1, 3), hex.slice(3, 5), hex.slice(5, 7)].map((channel) => Number.parseInt(channel, 16) / 255);
  const [red = 0, green = 0, blue = 0] = channels.map((channel) => channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4);
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
}
