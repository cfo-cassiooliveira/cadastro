// E2E do frontend do cadastro. Roda contra o docker compose (nginx em 127.0.0.1:4300), pelo scripts/e2e.sh.
const { defineConfig, devices } = require("@playwright/test");

module.exports = defineConfig({
  testDir: "./tests",
  globalSetup: require.resolve("./global-setup.js"),
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  reporter: [["list"]],
  use: {
    // localhost (e não host.docker.internal): o cookie __Host-cadastro exige contexto seguro, e em HTTP só
    // "localhost" é considerado seguro pelo navegador.
    baseURL: process.env.E2E_BASE_URL || "http://localhost:4300",
    locale: "pt-BR",
    trace: "retain-on-failure",
  },
  // Desktop, tablet e celular (frontend.md): toda tela precisa funcionar nas três classes de dispositivo.
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"] } },
    { name: "tablet", use: { ...devices["iPad (gen 7)"] } },
    { name: "mobile", use: { ...devices["iPhone 14"] } },
  ],
});
