// E2E da migração Legado -> Keycloak com a stack completa (Keycloak + Legado + Cadastro + Mailpit).
// Cada usuário de teste é criado pelo próprio teste no Legado, com login único e senha gerada em tempo de execução,
// e removido do Keycloak no final. O Legado não tem exclusão: as contas descartáveis ficam lá (ver e2e/README.md).
const { test, expect } = require("@playwright/test");
const svc = require("../support/services");
const { newTestUser, shortPassword, strongPassword } = require("../support/users");

const GENERIC_ERROR = "Usuário ou senha inválidos.";
const RESET_SENT = "Se houver uma conta para esse login ou e-mail, você receberá as instruções em instantes.";
const UPDATE_PASSWORD = "Sua senha precisa ser atualizada para a nova política de segurança.";

test.skip(!svc.isMigrationStackConfigured(), "stack de migração não configurada (use scripts/e2e.sh)");

/** Cria o usuário só no Legado e o registra para a limpeza e a checagem de LGPD. */
async function createLegacyUser(tag, deviceName, password) {
    const user = newTestUser(tag, deviceName, password);
    user.legacyId = await svc.registerLegacyUser(user);
    svc.recordTestUser({ device: deviceName, login: user.login, email: user.email });
    return user;
}

async function cleanUp(...users) {
    for (const user of users) {
        await svc.deleteKeycloakUser(user.login).catch(() => undefined);
        await svc.deleteMailsTo(user.email).catch(() => undefined);
    }
}

/** Abre uma página protegida do Cadastro (cai no login do Keycloak) e envia login e senha. */
async function signIn(page, login, password) {
    await page.goto("/profile");
    await submitCredentials(page, login, password);
}

async function submitCredentials(page, login, password) {
    await page.locator("#username").fill(login);
    await page.locator("#password").fill(password);
    await page.locator("#kc-login").click();
}

/**
 * Confirma que o login terminou no Cadastro, em "Meu perfil". No WebKit o cookie Secure de correlação do OIDC não é
 * aceito em http://localhost (limite do navegador de teste; em HTTPS funciona), então o retorno do BFF falha com
 * "?login=failed": nele só se confirma que o Keycloak autenticou e devolveu ao Cadastro, e a limitação é anotada.
 */
async function expectProfile(page) {
    if (test.info().project.use.defaultBrowserType === "webkit") {
        test.info().annotations.push({
            type: "limitação",
            description: "WebKit rejeita cookie Secure em http://localhost: retorno do BFF não validado",
        });
        await expect(page).toHaveURL(/localhost:4300/);
        return;
    }
    await expect(page).toHaveURL(/localhost:4300\/profile/);
    await expect(page.getByRole("heading", { level: 1, name: "Meu perfil" })).toBeVisible();
}

async function expectGenericError(page) {
    await expect(page.locator("#input-error")).toHaveText(GENERIC_ERROR);
}

test.describe("migração por login", () => {
    test.describe.configure({ mode: "serial" });
    let user;

    test.beforeAll(async ({}, info) => {
        user = await createLegacyUser("a", info.project.name);
    });
    test.afterAll(() => cleanUp(user));

    test("usuário só do Legado entra com login e senha, é migrado e vê Meu perfil", async ({ page }) => {
        expect(await svc.findKeycloakUser(user.login)).toBeNull();

        await signIn(page, user.login, user.password);

        await expectProfile(page);
        const migrated = await svc.findKeycloakUser(user.login);
        expect(migrated).not.toBeNull();
        expect((await svc.keycloakCredentials(migrated.id)).length).toBe(1);
        expect(await svc.waitUntilLegacyAccountIsMigrated(user.login, user.password)).toBe(true);
    });

    test("Legado: senha errada de conta migrada dá a mensagem genérica", async ({ page }) => {
        await page.goto(`${svc.config.legadoUrl}/login`);
        await page.getByLabel("Login").fill(user.login);
        await page.getByLabel("Senha", { exact: true }).fill(strongPassword());
        await svc.reserveLegadoSlot();
        await page.getByRole("button", { name: "Entrar" }).click();

        await expect(page.getByRole("alert")).toContainText("Login ou senha inválidos.");
        await expect(page.getByRole("alert")).not.toContainText("transferida");
    });

    test("Legado: senha certa de conta migrada mostra o aviso e redireciona ao Cadastro em 5 s", async ({ page }) => {
        await page.goto(`${svc.config.legadoUrl}/login`);
        await page.getByLabel("Login").fill(user.login);
        await page.getByLabel("Senha", { exact: true }).fill(user.password);
        await svc.reserveLegadoSlot();
        await page.getByRole("button", { name: "Entrar" }).click();

        await expect(page.getByRole("alert")).toContainText("Sua conta foi transferida para o novo sistema.");
        await expect(page.getByRole("link", { name: "Ir para o novo sistema" })).toBeVisible();
        await page.waitForURL(/localhost:4300/, { timeout: 12_000 });
    });

    test('Legado: "Ficar nesta página" cancela o redirecionamento', async ({ page }) => {
        await page.goto(`${svc.config.legadoUrl}/login`);
        await page.getByLabel("Login").fill(user.login);
        await page.getByLabel("Senha", { exact: true }).fill(user.password);
        await svc.reserveLegadoSlot();
        await page.getByRole("button", { name: "Entrar" }).click();
        await page.getByRole("button", { name: "Ficar nesta página" }).click();

        await page.waitForTimeout(7000);
        await expect(page).toHaveURL(/localhost:4200\/login/);
        await expect(page.getByRole("link", { name: "Ir para o novo sistema" })).toBeVisible();
    });

    test("segundo login do migrado não consulta o Legado", async ({ page }) => {
        await signIn(page, user.login, user.password);

        await expectProfile(page);
        // O scripts/e2e.sh confere nos logs da API do Legado: exatamente 1 verificação (a da migração) e nenhuma depois.
        svc.recordLegacyVerificationCheck(user.legacyId, 1);
    });
});

test("login com e-mail (e não só com o login) também migra", async ({ page }, info) => {
    const user = await createLegacyUser("b", info.project.name);
    try {
        await signIn(page, user.email, user.password);

        await expectProfile(page);
        expect(await svc.findKeycloakUser(user.login)).not.toBeNull();
    } finally {
        await cleanUp(user);
    }
});

test("senha fora da política do realm exige atualizar a senha antes de entrar", async ({ page }, info) => {
    const user = await createLegacyUser("c", info.project.name, shortPassword());
    try {
        await signIn(page, user.login, user.password);

        await expect(page.getByText(UPDATE_PASSWORD)).toBeVisible();
        await expect(page.locator("#password-new")).toBeVisible();

        // Senha ainda fora da política não conclui o login.
        await page.locator("#password-new").fill("Curta1!");
        await page.locator("#password-confirm").fill("Curta1!");
        await page.locator("form button[type=submit], form input[type=submit]").first().click();
        await expect(page.locator("#password-new")).toBeVisible();
        await expect(page).not.toHaveURL(/localhost:4300/);

        const valid = strongPassword();
        await page.locator("#password-new").fill(valid);
        await page.locator("#password-confirm").fill(valid);
        await page.locator("form button[type=submit], form input[type=submit]").first().click();

        await expectProfile(page);
    } finally {
        await cleanUp(user);
    }
});

test("senha errada de usuário do Legado e usuário inexistente dão a mesma mensagem e não criam nada", async ({
    page,
}, info) => {
    const user = await createLegacyUser("d", info.project.name);
    const ghost = newTestUser("x", info.project.name);
    svc.recordTestUser({ device: info.project.name, login: ghost.login, email: ghost.email });
    try {
        await signIn(page, user.login, strongPassword());
        await expectGenericError(page);
        expect(await svc.findKeycloakUser(user.login)).toBeNull();

        await submitCredentials(page, ghost.email, ghost.password);
        await expectGenericError(page);
        expect(await svc.findKeycloakUser(ghost.login)).toBeNull();
    } finally {
        await cleanUp(user, ghost);
    }
});

test("reset de senha de usuário só do Legado: e-mail, senha nova, entra e o Legado marca depois", async ({
    page,
}, info) => {
    const user = await createLegacyUser("e", info.project.name);
    const newPassword = strongPassword();
    try {
        await page.goto("/profile");
        await page.locator('a[href*="reset-credentials"]').click();
        await page.locator("#username").fill(user.login);
        await page.locator("form button[type=submit], form input[type=submit]").first().click();
        await expect(page.getByText(RESET_SENT)).toBeVisible();

        const mailText = await svc.waitForMailText(user.email);
        const link = mailText.match(/https?:\/\/localhost:8180\/\S+/)?.[0];
        expect(link).toBeTruthy();

        // Antes de definir a senha: usuário criado sem senha e o Legado ainda não o marcou como migrado.
        const shell = await svc.findKeycloakUser(user.login);
        expect(shell).not.toBeNull();
        expect(await svc.keycloakCredentials(shell.id)).toEqual([]);
        expect((await svc.legacyLogin(user.login, user.password)).status).toBe(200);

        await page.goto(link);
        const proceed = page.getByRole("link", { name: /clique aqui|prosseguir|continue/i });
        if (await proceed.isVisible().catch(() => false)) await proceed.click();
        await page.locator("#password-new").fill(newPassword);
        await page.locator("#password-confirm").fill(newPassword);
        await page.locator("form button[type=submit], form input[type=submit]").first().click();

        await expectProfile(page);
        expect(await svc.waitUntilLegacyAccountIsMigrated(user.login, user.password)).toBe(true);
    } finally {
        await cleanUp(user);
    }
});

test("reset de usuário inexistente mostra a mesma mensagem e não envia e-mail", async ({ page }, info) => {
    const ghost = newTestUser("y", info.project.name);
    svc.recordTestUser({ device: info.project.name, login: ghost.login, email: ghost.email });
    await page.goto("/profile");
    await page.locator('a[href*="reset-credentials"]').click();
    await page.locator("#username").fill(ghost.email);
    await page.locator("form button[type=submit], form input[type=submit]").first().click();

    await expect(page.getByText(RESET_SENT)).toBeVisible();
    await page.waitForTimeout(5000);
    expect(await svc.mailsTo(ghost.email)).toEqual([]);
    expect(await svc.findKeycloakUser(ghost.login)).toBeNull();
});

test("conta do Legado bloqueada após 5 falhas continua com a mensagem genérica", async ({ page }, info) => {
    const user = await createLegacyUser("f", info.project.name);
    try {
        await page.goto("/profile");
        for (let attempt = 0; attempt < 5; attempt += 1) {
            await submitCredentials(page, user.login, strongPassword());
            await expectGenericError(page);
        }

        // Com a conta bloqueada, nem a senha correta entra.
        await submitCredentials(page, user.login, user.password);
        await expectGenericError(page);
        expect(await svc.findKeycloakUser(user.login)).toBeNull();
    } finally {
        await cleanUp(user);
    }
});
