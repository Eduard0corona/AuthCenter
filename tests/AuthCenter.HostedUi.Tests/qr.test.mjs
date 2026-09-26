import test from "node:test";
import assert from "node:assert/strict";
import jsQR from "jsqr";
import { qrModules } from "../../src/AuthCenter.Api/wwwroot/assets/qr.js";

// Renders the modules as an RGBA image with a quiet zone and decodes it with an independent reader.
function decode(text, scale = 4) {
  const { size, modules } = qrModules(text);
  const width = (size + 8) * scale;
  const pixels = new Uint8ClampedArray(width * width * 4).fill(255);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      if (!modules[y][x]) continue;
      for (let dy = 0; dy < scale; dy++) {
        for (let dx = 0; dx < scale; dx++) {
          const offset = (((y + 4) * scale + dy) * width + (x + 4) * scale + dx) * 4;
          pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
        }
      }
    }
  }
  return { size, result: jsQR(pixels, width, width, { inversionAttempts: "dontInvert" }) };
}

test("an authenticator enrollment URI decodes back to the same bytes", () => {
  const uri = "otpauth://totp/AuthCenter:ana.garcia%40example.com?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=AuthCenter&algorithm=SHA1&digits=6&period=30";
  const { result } = decode(uri);
  assert.ok(result, "the code is readable");
  assert.deepEqual(Uint8Array.from(result.binaryData), new TextEncoder().encode(uri));
});

test("every version size used by realistic texts is readable", () => {
  const seen = new Set();
  for (const length of [1, 14, 15, 16, 26, 27, 43, 44, 60, 63, 84, 85, 106, 107, 122, 123, 152, 153, 180, 213, 251, 287, 331, 362, 412, 450, 504, 560, 624, 666]) {
    const text = Array.from({ length }, (_, i) => String.fromCharCode(33 + ((i * 7 + length) % 90))).join("");
    const { size, result } = decode(text);
    seen.add(size);
    assert.ok(result, `length ${length} (size ${size}) is readable`);
    assert.equal(result.data, text, `length ${length}`);
  }
  // Covers versions without and with version information blocks (7+).
  assert.ok([...seen].some(size => size >= 45), "includes version 7 or later");
});

test("non-ASCII text is encoded as UTF-8", () => {
  const text = "Autenticación · 認証 · ñandú";
  const { result } = decode(text);
  assert.ok(result);
  assert.deepEqual(Uint8Array.from(result.binaryData), new TextEncoder().encode(text));
});

test("the encoder rejects text longer than the largest code", () => {
  assert.throws(() => qrModules("x".repeat(3000)), RangeError);
});
