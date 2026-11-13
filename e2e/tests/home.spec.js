// E2E das telas públicas: Início (visitante) e o redirecionamento das rotas protegidas para o login do Keycloak.
const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;

test.describe("Início (visitante)", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Seu cadastro do Workspace em um só lugar");
  });

  test("tem um único h1, idioma pt-BR, noindex e título", async ({ page }) => {
    await expect(page.locator("h1")).toHaveCount(1);
    await expect(page.locator("html")).toHaveAttribute("lang", "pt-BR");
    await expect(page.locator('meta[name="robots"]')).toHaveAttribute("content", /noindex/);
    await expect(page).toHaveTitle("Cadastro — Workspace");
  });

  test("não tem violação séria ou crítica do axe", async ({ page }) => {
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
    const blocking = results.violations
      .filter((v) => ["serious", "critical"].includes(v.impact))
      .map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);

    expect(blocking).toEqual([]);
  });

  test("não gera rolagem horizontal", async ({ page }) => {
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);

    expect(overflow).toBe(false);
  });

  test("atalho 'Pular para o conteúdo' leva o foco ao conteúdo principal", async ({ page, browserName }) => {
    // O WebKit só move o foco por Tab para links com a opção do sistema ligada; o atalho é validado no Chromium.
    test.skip(browserName === "webkit", "Tab não foca links no WebKit por padrão");
    await page.keyboard.press("Tab");
    await expect(page.getByRole("link", { name: "Pular para o conteúdo" })).toBeFocused();

    await page.keyboard.press("Enter");
    await expect(page.locator("main#main-content")).toBeFocused();
  });

  test("'Entrar' leva à tela de login do Keycloak", async ({ page }) => {
    await page.getByRole("main").getByRole("button", { name: "Entrar" }).click();

    await expect(page).toHaveURL(/localhost:8180\/realms\/workspace\//);
    await expect(page).toHaveTitle(/Entrar/);
  });
});

test("visitante em página protegida vai ao login do Keycloak", async ({ page }) => {
  await page.goto("/profile");

  await expect(page).toHaveURL(/localhost:8180\/realms\/workspace\//);
});

test("Início carrega sem erros no console", async ({ page }) => {
  const errors = [];
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(message.text());
  });
  page.on("pageerror", (error) => errors.push(error.message));

  await page.goto("/", { waitUntil: "networkidle" });

  expect(errors).toEqual([]);
});

test("volta do logout mostra 'Você saiu da sua conta.' e limpa a URL", async ({ page }) => {
  await page.goto("/?state=signed-out");

  await expect(page.getByRole("status")).toContainText("Você saiu da sua conta.");
  await expect(page).toHaveURL(/\/$/);
});
