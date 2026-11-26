// E2E REAL da importação em lote (spec 002, E1): stack de pé (sem mocks, sem Testcontainers) e execução no HOST,
// pois o teste usa o CLI do Docker (parar o Keycloak, ler logs de containers, medir memória). Rodar por
// scripts/e2e-import.sh. Separado de e2e/playwright.config.js: o E2E padrão (container) não executa este fluxo.
const { defineConfig, devices } = require("@playwright/test");

module.exports = defineConfig({
  testDir: ".",
  testMatch: "import-real.spec.js",
  workers: 1,
  fullyParallel: false,
  // O lote de 5.000 linhas leva ~9 min (10 criações/s) e o teste cobre vários cenários em sequência.
  timeout: 90 * 60_000,
  expect: { timeout: 30_000 },
  retries: 0,
  forbidOnly: true,
  reporter: [["list"]],
  outputDir: "../.artifacts/import-real/test-results",
  use: {
    // "localhost": o cookie __Host-cadastro exige contexto seguro e o issuer do Keycloak é localhost:8180.
    baseURL: process.env.E2E_BASE_URL || "http://localhost:4300",
    locale: "pt-BR",
    trace: "retain-on-failure",
    ...devices["Desktop Chrome"],
  },
});
