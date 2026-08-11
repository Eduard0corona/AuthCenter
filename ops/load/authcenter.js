import http from "k6/http";
import { check, sleep } from "k6";

const baseUrl = (__ENV.AUTHCENTER_BASE_URL || "").replace(/\/$/, "");
if (!baseUrl.startsWith("https://")) throw new Error("AUTHCENTER_BASE_URL must be HTTPS.");

export const options = {
  scenarios: {
    public_contract: { executor: "constant-vus", vus: 5, duration: __ENV.K6_DURATION || "1m", exec: "publicContract" },
    authenticated_login: { executor: "constant-arrival-rate", rate: 1, timeUnit: "1s", duration: __ENV.K6_DURATION || "1m", preAllocatedVUs: 2, exec: "login", startTime: "2s" }
  },
  thresholds: {
    "http_req_failed{operation:public}": ["rate<0.001"],
    "http_req_duration{operation:public}": ["p(95)<400"],
    "http_req_failed{operation:login}": ["rate<0.01"],
    "http_req_duration{operation:login}": ["p(95)<750"]
  }
};

export function publicContract() {
  const live = http.get(`${baseUrl}/health/live`, { tags: { operation: "public" } });
  check(live, { "liveness 200": response => response.status === 200 });
  const discovery = http.get(`${baseUrl}/.well-known/openid-configuration`, { tags: { operation: "public" } });
  check(discovery, { "discovery 200": response => response.status === 200 && response.json("issuer") });
  sleep(0.2);
}

export function login() {
  if (!__ENV.AUTHCENTER_TEST_EMAIL || !__ENV.AUTHCENTER_TEST_PASSWORD) return;
  const response = http.post(`${baseUrl}/api/auth/login`, JSON.stringify({
    email: __ENV.AUTHCENTER_TEST_EMAIL,
    password: __ENV.AUTHCENTER_TEST_PASSWORD,
    applicationCode: __ENV.AUTHCENTER_APPLICATION_CODE || "AUTHCENTER"
  }), { headers: { "Content-Type": "application/json" }, tags: { operation: "login" } });
  check(response, { "login is not server error": value => value.status < 500 });
}
