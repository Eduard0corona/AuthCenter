import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  base: "/admin-v2/",
  plugins: [react()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    assetsDir: "assets",
    manifest: true,
    rollupOptions: {
      output: {
        entryFileNames: "assets/[name]-[hash].js",
        chunkFileNames: "assets/[name]-[hash].js",
        assetFileNames: "assets/[name]-[hash][extname]"
      }
    }
  },
  server: {
    port: 5174,
    proxy: {
      "/api": "https://localhost:7001",
      "/ui-api": "https://localhost:7001",
      "/login": "https://localhost:7001",
      "/portal": "https://localhost:7001"
    }
  },
  test: {
    environment: "jsdom",
    setupFiles: "./src/test/setup.ts",
    globals: true,
    include: ["src/**/*.test.{ts,tsx}"],
    css: true,
    coverage: {
      // The logic modules (API client, errors, schemas, formatters, hooks). The pages are covered
      // by the Playwright suite, which vitest cannot measure.
      provider: "v8",
      include: ["src/**/*.ts"],
      exclude: ["src/**/*.test.ts", "src/test/**", "src/**/*.d.ts"],
      reporter: ["text-summary", "cobertura", "html"],
      reportsDirectory: "coverage",
      thresholds: { lines: 70, statements: 68, branches: 58, functions: 65 }
    }
  }
});
