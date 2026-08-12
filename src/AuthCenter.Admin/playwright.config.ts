import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: true,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? "github" : "list",
  use: {
    baseURL: "http://127.0.0.1:5174",
    trace: "retain-on-failure"
  },
  projects: [
    { name: "chromium-desktop", use: { ...devices["Desktop Chrome"] } },
    { name: "chromium-tablet", use: { ...devices["Desktop Chrome"], viewport: { width: 768, height: 1024 }, hasTouch: true } },
    { name: "chromium-mobile", use: { ...devices["Pixel 7"] } }
  ],
  webServer: {
    command: "npm run dev -- --host 127.0.0.1",
    url: "http://127.0.0.1:5174/admin-v2/",
    reuseExistingServer: !process.env.CI,
    timeout: 120_000
  }
});
