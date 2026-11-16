import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./tests",
  timeout: 20_000,
  fullyParallel: false,
  use: {
    baseURL: "http://127.0.0.1:5179",
    browserName: "chromium",
    channel: "msedge",
    viewport: { width: 1360, height: 900 },
    headless: true,
    trace: "retain-on-failure",
  },
  webServer: {
    command: "npm run dev -- --port 5179 --strictPort",
    url: "http://127.0.0.1:5179",
    reuseExistingServer: false,
    timeout: 20_000,
  },
  reporter: "line",
});
