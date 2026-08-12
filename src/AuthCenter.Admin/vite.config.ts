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
    css: true
  }
});
