// X01 (graylog/docs/specs/001-pseudonimizacao-logs): E2E ponta a ponta SEM GELF simulado. O mesmo login/e-mail passa
// pelo Legado (cadastro e login) e pelo Cadastro (login via migração sob demanda do Keycloak); o investigador pesquisa
// pela tela /log-search e acha os eventos reais dos dois serviços; a trilha de auditoria existe; e nenhum valor de teste
// em claro (login, e-mail, celular, senha) aparece em nenhuma mensagem do Graylog.
const { randomBytes } = require("node:crypto");
const { test, expect } = require("@playwright/test");
const svc = require("../support/services");
const { strongPassword } = require("../support/users");
const { totp } = require("../support/totp");

test.skip(!svc.isMigrationStackConfigured(), "stack de migração não configurada (use scripts/e2e.sh)");

const TOTP_STEP_MS = 31_000;
const INDEXING_TIMEOUT_MS = 120_000;
const CASE_REFERENCE = "X01-QA-2026";

const epoch = Math.floor(Date.now() / 1000);
const subject = {
    login: `x01qa${epoch}`,
    email: `x01qa${epoch}@exemplo.com`,
    // Celular fictício válido (DDD 11, 9 dígitos) único por execução.
    mobilePhone: `119${String(epoch).slice(-8)}`,
    password: strongPassword(),
};
const investigator = {
    login: `x01inv${epoch}`,
    email: `x01inv${epoch}@exemplo.com`,
    password: strongPassword(),
    // Segredo TOTP ASCII (hex maiúsculo), importado como credencial OTP do investigador.
    otpSecret: randomBytes(10).toString("hex").toUpperCase(),
};
const startedAt = new Date(Date.now() - 60_000).toISOString();

/** Nome do serviço de origem do evento no Graylog. */
const serviceOf = (message) => message.service_name ?? message.application ?? message.source;

/** Executa a consulta devolvida pela tela no Graylog e espera os serviços esperados aparecerem. */
async function searchUntil(query, predicate) {
    const deadline = Date.now() + INDEXING_TIMEOUT_MS;
    for (;;) {
        const messages = await svc.graylogMessagesSince(startedAt, query);
        if (predicate(messages) || Date.now() > deadline) return messages;
        await svc.sleep(5000);
    }
}

test.describe("X01 pesquisa por token com eventos reais", () => {
    test.describe.configure({ mode: "serial" });

    let investigatorId;

    test.beforeAll(async ({}, info) => {
        test.skip(info.project.name !== "desktop", "fluxo único: roda só no desktop");
        investigatorId = await svc.createInvestigator(investigator);
    });

    test.afterAll(async () => {
        await svc.deleteKeycloakUser(subject.login).catch(() => undefined);
        await svc.deleteKeycloakUser(investigator.login).catch(() => undefined);
    });

    test("mesmo login/e-mail no Legado e no Cadastro, pesquisa pela tela e nenhum valor em claro", async ({
        browser,
        page,
    }) => {
        test.setTimeout(600_000);

        // 1) Fluxo real: cadastro e login no Legado, depois login no Cadastro (migra o usuário para o Keycloak).
        await svc.registerLegacyUser({ ...subject, login: subject.login });
        svc.recordTestUser({ device: "desktop", login: subject.login, email: subject.email });
        const legacyLogin = await svc.legacyLogin(subject.login, subject.password);
        expect(legacyLogin.status).toBe(200);

        await page.goto("/profile");
        await page.locator("#username").fill(subject.login);
        await page.locator("#password").fill(subject.password);
        await page.locator("#kc-login").click();
        await expect(page.getByRole("heading", { level: 1, name: "Meu perfil" })).toBeVisible();

        // 2) Investigador (contexto limpo): login com OTP, step-up quando pedido e consulta pela tela.
        const context = await browser.newContext({ locale: "pt-BR" });
        const screen = await context.newPage();
        await screen.goto("/log-search");
        await screen.locator("#username").fill(investigator.login);
        await screen.locator("#password").fill(investigator.password);
        await screen.locator("#kc-login").click();
        await screen.getByRole("textbox", { name: "Código de uso único" }).fill(totp(investigator.otpSecret));
        await screen.getByRole("button", { name: "Entrar" }).click();
        await expect(screen.getByRole("heading", { name: "Pesquisa em logs" })).toBeVisible();

        /** Preenche o formulário, gera a consulta (com step-up de OTP se pedido) e devolve a consulta do Graylog. */
        async function generateQuery(dataType, value) {
            const submit = async () => {
                await screen.getByLabel("Tipo de dado").selectOption({ label: dataType });
                await screen.getByLabel("Valor", { exact: true }).fill(value);
                await screen.getByLabel("Referência do caso").fill(CASE_REFERENCE);
                await screen.getByRole("button", { name: "Gerar consulta" }).click();
            };
            await submit();
            const stepUp = screen.getByRole("button", { name: "Confirmar com código" });
            await stepUp.waitFor({ timeout: 10_000 }).catch(() => undefined);
            if (await stepUp.isVisible()) {
                await stepUp.click();
                // O Keycloak recusa reuso do mesmo código TOTP: espera o próximo passo.
                await svc.sleep(TOTP_STEP_MS);
                await screen.getByRole("textbox", { name: "Código de uso único" }).fill(totp(investigator.otpSecret));
                await screen.getByRole("button", { name: "Entrar" }).click();
                await expect(screen.getByRole("heading", { name: "Pesquisa em logs" })).toBeVisible();
                await submit();
            }
            await expect(screen.locator("main")).toContainText("_token", { timeout: 15_000 });
            const text = await screen.locator("main").innerText();
            // A tela nunca mostra o valor pesquisado.
            expect(text).not.toContain(value);
            const query = /Consulta do Graylog\s+([^\n]+)/.exec(text)?.[1];
            expect(query, "consulta do Graylog exibida pela tela").toBeTruthy();
            return query;
        }

        // 3a) Token do login: eventos reais do Legado (cadastro e login) e do Cadastro (login).
        const loginQuery = await generateQuery("Login", subject.login);
        const loginHits = await searchUntil(loginQuery, (ms) => {
            const services = new Set(ms.map(serviceOf));
            return services.has("legado-api") && services.has("cadastro-api");
        });
        const loginServices = new Set(loginHits.map(serviceOf));
        expect([...loginServices]).toEqual(expect.arrayContaining(["legado-api", "cadastro-api"]));
        for (const hit of loginHits) {
            // Evento de aplicação real (não o GELF simulado do QA anterior) e com o valor mascarado.
            expect(hit.UserName_token, `${serviceOf(hit)} sem UserName_token`).toMatch(/^k\d+-/);
            expect(hit.UserName).not.toBe(subject.login);
        }
        expect(loginHits.filter((m) => serviceOf(m) === "legado-api").length).toBeGreaterThanOrEqual(2);

        // 3b) Token do e-mail: só o Legado loga o e-mail (registro).
        const emailQuery = await generateQuery("E-mail", subject.email);
        const emailHits = await searchUntil(emailQuery, (ms) => ms.some((m) => serviceOf(m) === "legado-api"));
        expect(emailHits.map(serviceOf)).toContain("legado-api");
        expect(emailHits.map(serviceOf)).not.toContain("cadastro-api");
        expect(emailHits.every((m) => /^k\d+-/.test(m.Email_token ?? ""))).toBe(true);

        // 3c) Token do celular: registro no Legado.
        const phoneQuery = await generateQuery("Celular/telefone", subject.mobilePhone);
        const phoneHits = await searchUntil(phoneQuery, (ms) => ms.some((m) => serviceOf(m) === "legado-api"));
        expect(phoneHits.map(serviceOf)).toContain("legado-api");
        expect(phoneHits.every((m) => /^k\d+-/.test(m.MobilePhone_token ?? ""))).toBe(true);
        await context.close();

        // 4a) Trilha de auditoria: uma entrada por consulta, com o investigador (sub) e sem valor.
        const audit = await searchUntil("audit_event:log_search_query_issued", (ms) =>
            ["login", "email", "phone"].every((t) => ms.some((m) => m.data_type === t && m.investigator_id === investigatorId)),
        );
        const mine = audit.filter((m) => m.investigator_id === investigatorId);
        expect(mine.map((m) => m.data_type).sort()).toEqual(["email", "login", "phone"]);
        for (const entry of mine) {
            expect(entry.case_reference).toBe(CASE_REFERENCE);
        }

        // 4b) Nenhum valor de teste em claro em NENHUM campo/stream: varredura de todas as mensagens do período e
        // buscas de texto pelos próprios valores.
        await svc.sleep(10_000);
        const everything = await svc.graylogMessagesSince(startedAt);
        expect(everything.length).toBeGreaterThan(0);
        const secrets = [subject.login, subject.email, subject.mobilePhone, subject.password, investigator.password];
        const leaks = [];
        for (const message of everything) {
            const text = JSON.stringify(message).toLowerCase();
            for (const value of secrets) {
                if (text.includes(value.toLowerCase())) leaks.push(`${serviceOf(message)}/${message.type ?? "?"}`);
            }
        }
        expect(leaks).toEqual([]);
        for (const value of [subject.login, subject.email, subject.mobilePhone]) {
            expect(await svc.graylogMessagesSince(startedAt, `"${value}"`)).toEqual([]);
        }
    });
});
