import http from "k6/http";
import { check } from "k6";

// Directory reads against a large directory, the operations the console and support use most.
// Seed a NON-PRODUCTION environment with ops/load/seed-large-directory.sql first, then run with an
// access token of an AUTHCENTER administrator (it lasts Jwt:AccessTokenMinutes):
//   AUTHCENTER_BASE_URL=https://identity-staging.example.com AUTHCENTER_ADMIN_TOKEN=... k6 run ops/load/directory.js
const baseUrl = (__ENV.AUTHCENTER_BASE_URL || "").replace(/\/$/, "");
if (!baseUrl.startsWith("https://")) throw new Error("AUTHCENTER_BASE_URL must be HTTPS.");
const token = __ENV.AUTHCENTER_ADMIN_TOKEN;
if (!token) throw new Error("Set AUTHCENTER_ADMIN_TOKEN to an administrator's access token.");
const prefix = __ENV.AUTHCENTER_SEED_PREFIX || "scale";
const users = Number(__ENV.AUTHCENTER_SEED_USERS || 100000);

export const options = {
  scenarios: {
    directory: {
      executor: "constant-arrival-rate",
      rate: Number(__ENV.K6_RATE || 5),
      timeUnit: "1s",
      duration: __ENV.K6_DURATION || "2m",
      preAllocatedVUs: 20,
      exec: "directory"
    }
  },
  // The Directory/SCIM objective of docs/operations/SLO.md.
  thresholds: {
    "http_req_failed{operation:directory}": ["rate<0.01"],
    "http_req_duration{operation:directory}": ["p(95)<800"]
  }
};

export function directory() {
  const headers = { Authorization: `Bearer ${token}` };
  const n = 1 + Math.floor(Math.random() * users);
  const pick = Math.random();
  const request = pick < 0.4
    ? ["users-search", `/api/users?search=${encodeURIComponent(`${prefix}-${n}@`)}&pageSize=20`]
    : pick < 0.7
      ? ["users-page", `/api/users?page=${1 + Math.floor(Math.random() * 50)}&pageSize=50`]
      : pick < 0.85
        ? ["groups", `/api/groups?search=${encodeURIComponent(`${prefix} group`)}&pageSize=50`]
        : ["dashboard", "/api/admin-dashboard"];
  const response = http.get(`${baseUrl}${request[1]}`, { headers, tags: { operation: "directory", name: request[0] } });
  check(response, { "directory 200": (value) => value.status === 200 });
}
