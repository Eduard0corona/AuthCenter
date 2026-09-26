import { generateKeyPairSync, randomBytes } from "node:crypto";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig, devices } from "@playwright/test";

// End-to-end tests of the hosted pages (login, portal, emailed links) against the real API,
// started from the Release build with a throwaway SQL Server database and emails written to a
// pickup directory. Requires AUTHCENTER_RELATIONAL_TEST_CONNECTION (a server, no database).
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const port = Number(process.env.HOSTED_UI_PORT ?? 5071);
const baseURL = `http://localhost:${port}`;
const connection = process.env.AUTHCENTER_RELATIONAL_TEST_CONNECTION;
if (!connection) throw new Error("Set AUTHCENTER_RELATIONAL_TEST_CONNECTION to a SQL Server (without a database) to run the hosted UI end-to-end tests.");

// Generated once by the runner; the workers inherit them through the environment.
process.env.HOSTED_UI_BASE_URL = baseURL;
process.env.HOSTED_UI_MAIL_DIR ??= mkdtempSync(path.join(tmpdir(), "authcenter-mail-"));
process.env.HOSTED_UI_DATABASE ??= `AuthCenter_HostedUi_${randomBytes(6).toString("hex")}`;
process.env.HOSTED_UI_ADMIN_EMAIL ??= "hosted-ui-admin@authcenter.test";
process.env.HOSTED_UI_ADMIN_PASSWORD ??= `Adm1n-${randomBytes(12).toString("base64url")}`;
process.env.HOSTED_UI_SIGNING_KEY ??= generateKeyPairSync("rsa", {
  modulusLength: 2048,
  privateKeyEncoding: { type: "pkcs8", format: "pem" },
  publicKeyEncoding: { type: "spki", format: "pem" }
}).privateKey;

const executablePath = process.env.HOSTED_UI_CHROMIUM || undefined;

export default defineConfig({
  testDir: "./e2e",
  testMatch: "*.spec.mjs",
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: true,
  workers: process.env.CI ? 2 : 4,
  retries: 0,
  reporter: process.env.CI ? [["line"], ["html", { open: "never", outputFolder: "playwright-report" }]] : [["line"]],
  outputDir: process.env.HOSTED_UI_OUTPUT_DIR ?? "test-results",
  use: {
    baseURL,
    locale: "es-ES",
    trace: "retain-on-failure",
    launchOptions: executablePath ? { executablePath } : {}
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], launchOptions: executablePath ? { executablePath } : {} } }],
  webServer: {
    command: `dotnet run --project "${path.join(root, "src/AuthCenter.Api/AuthCenter.Api.csproj")}" --configuration Release --no-build --no-launch-profile --urls ${baseURL}`,
    url: `${baseURL}/health/live`,
    timeout: 240_000,
    reuseExistingServer: false,
    stdout: process.env.HOSTED_UI_SERVER_LOG ? "pipe" : "ignore",
    stderr: "pipe",
    env: {
      ASPNETCORE_ENVIRONMENT: "Development",
      ConnectionStrings__DefaultConnection: `${connection};Database=${process.env.HOSTED_UI_DATABASE};MultipleActiveResultSets=true`,
      Database__MigrateOnStartup: "true",
      Database__SeedOnStartup: "true",
      Seed__AdminEmail: process.env.HOSTED_UI_ADMIN_EMAIL,
      Seed__AdminPassword: process.env.HOSTED_UI_ADMIN_PASSWORD,
      Seed__AdminFullName: "Hosted UI Admin",
      Jwt__RsaPrivateKeyPem: process.env.HOSTED_UI_SIGNING_KEY,
      Jwt__Issuer: baseURL,
      Oidc__PublicOrigin: baseURL,
      Mfa__EncryptionKey: randomBytes(32).toString("base64"),
      Email__DevelopmentPickupDirectory: process.env.HOSTED_UI_MAIL_DIR,
      ActionLinks__DefaultBaseUrl: baseURL,
      ActionLinks__ApplicationBaseUrls__AUTHCENTER: baseURL,
      Passkeys__RelyingPartyId: "localhost",
      Passkeys__AllowedOrigins__0: baseURL,
      RateLimiting__Enabled: "false",
      RateLimiting__DistributedEnabled: "false",
      Logging__LogLevel__Default: "Warning",
      Serilog__MinimumLevel__Default: "Warning"
    }
  }
});
