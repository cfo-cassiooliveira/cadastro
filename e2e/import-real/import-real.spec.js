// E1 (spec 002): importação em lote 1x1 de PONTA A PONTA com a stack real (BFF, ClamAV, SQL Server, RabbitMQ, Keycloak,
// Mailpit, Legado). Sem mocks. Roda no host por scripts/e2e-import.sh. Todos os dados são fictícios (ver support/csv.js).
// Resultados numéricos vão para <out>/results.json (alimenta o e2e-report.md).
const fs = require("node:fs");
const path = require("node:path");
const { randomBytes, createHash } = require("node:crypto");
const { test, expect } = require("@playwright/test");
const stack = require("./support/stack");
const { buildCsv } = require("./support/csv");
const { totp } = require("../support/totp");
const { strongPassword } = require("../support/users");
const svc = require("../support/services");

const RUN = Date.now().toString(36);
const OUT = process.env.E2E_IMPORT_OUT || path.join(__dirname, "..", ".artifacts", "import-real");
const VALID = Number(process.env.E2E_IMPORT_VALID || 4950);
const INVALID = Number(process.env.E2E_IMPORT_INVALID || 20);
const DUPLICATES = Number(process.env.E2E_IMPORT_DUPLICATES || 30);
const SMALL_VALID = Number(process.env.E2E_IMPORT_SMALL_VALID || 400);
const SMALL_REJECTED = 3;
const TAP = `e2e.tap.${RUN}`;
const SECOND = 1000;
const ACTIVATION_LIFESPAN_S = 259_200;

fs.mkdirSync(OUT, { recursive: true });
const startedAt = new Date().toISOString();
const results = { runId: RUN, startedAt, params: { VALID, INVALID, DUPLICATES, SMALL_VALID, SMALL_REJECTED }, steps: {}, memoryPeakMiB: {} };
const save = () => fs.writeFileSync(path.join(OUT, "results.json"), JSON.stringify(results, null, 2));
const log = (...args) => console.log(`[${new Date().toISOString().slice(11, 19)}]`, ...args);

const importer = {
    login: `e1imp${RUN}`,
    email: `e1imp.${RUN}@example.test`,
    password: strongPassword(),
    otpSecret: randomBytes(10).toString("hex").toUpperCase(),
    role: "user-importer",
};
const plain = { login: `e1plain${RUN}`, email: `e1plain.${RUN}@example.test`, password: strongPassword() };

// Arquivos de teste (dados fictícios; ficam em <out>, fora do git).
const big = buildCsv({ runId: RUN, valid: VALID, invalid: INVALID, duplicates: DUPLICATES });
const smallRun = `${RUN}s`;
const small = buildCsv({ runId: smallRun, valid: SMALL_VALID, rejectedByIdentity: SMALL_REJECTED });
const bigFile = path.join(OUT, `lote-grande-${RUN}.csv`);
const smallFile = path.join(OUT, `lote-pequeno-${RUN}.csv`);
const headerFile = path.join(OUT, `cabecalho-invalido-${RUN}.csv`);
const tinyFile = path.join(OUT, `tiny-${RUN}.csv`);
fs.writeFileSync(bigFile, big.text);
fs.writeFileSync(smallFile, small.text);
fs.writeFileSync(headerFile, "name,mail\nx,y\n");
const tiny = buildCsv({ runId: `${RUN}t`, valid: 2 });
fs.writeFileSync(tinyFile, tiny.text);

// Marcadores de dado pessoal da varredura LGPD (CSV grande e pequeno, linhas inválidas incluídas).
const cpfDigits = (cpf) => cpf.replace(/\D/g, "");
const allPeople = [...big.people, ...small.people, ...tiny.people];
const sensitive = {
    runMarkers: [RUN, smallRun, `${RUN}t`],
    cpfs: new Set(allPeople.map((p) => cpfDigits(p.cpf))),
    nameMarker: "Sintetico",
};

test.describe.configure({ mode: "serial" });

test.describe("E1 importação em lote real", () => {
    let page;
    let context;
    let lastTotpStep = 0;
    let bigBatchId;
    let smallBatchId;
    let bigConfirmedAt;
    // Mensagens retiradas da fila-espelho no teste de idempotência: continuam entrando na varredura LGPD.
    const drainedTap = [];
    const sampleTimer = { handle: null };

    /** Espera um passo TOTP novo (o Keycloak recusa reuso do código) e devolve o código. */
    async function freshTotp() {
        let step = Math.floor(Date.now() / 30_000);
        if (step <= lastTotpStep) {
            await svc.sleep((lastTotpStep + 1) * 30_000 - Date.now() + 1500);
            step = Math.floor(Date.now() / 30_000);
        }
        lastTotpStep = step;
        return totp(importer.otpSecret);
    }

    /** Preenche o formulário do Keycloak quando ele aparecer (login e/ou OTP) e espera voltar ao Cadastro. */
    async function completeKeycloak(target) {
        for (let i = 0; i < 4; i += 1) {
            const username = page.locator("#username");
            const otp = page.getByRole("textbox", { name: "Código de uso único" });
            await Promise.race([
                username.waitFor({ timeout: 15_000 }).catch(() => undefined),
                otp.waitFor({ timeout: 15_000 }).catch(() => undefined),
                target.waitFor({ timeout: 15_000 }).catch(() => undefined),
            ]);
            if (await target.isVisible().catch(() => false)) return;
            if (await username.isVisible().catch(() => false)) {
                await username.fill(importer.login);
                await page.locator("#password").fill(importer.password);
                await page.locator("#kc-login").click();
            } else if (await otp.isVisible().catch(() => false)) {
                await otp.fill(await freshTotp());
                await page.getByRole("button", { name: "Entrar" }).click();
            }
        }
        await expect(target).toBeVisible();
    }

    const uploadHeading = () => page.getByRole("heading", { level: 1, name: "Enviar lista de pessoas" });
    const listHeading = () => page.getByRole("heading", { level: 1, name: "Importações em lote" });

    /** Reabre o login do Keycloak com acr_values=otp (OTP recente) e volta à lista. */
    async function stepUp() {
        log("step-up de OTP");
        await page.goto("/bff/login?returnUrl=%2Fimports&acr_values=otp");
        await completeKeycloak(listHeading());
    }

    /** GET na API com o cookie da sessão; em 403 (sessão sem OTP; leituras não vencem em 5 min) faz step-up e repete. */
    async function api(route) {
        let response = await page.request.get(route);
        if (response.status() === 403) {
            results.otpStepUps = (results.otpStepUps ?? 0) + 1;
            await stepUp();
            response = await page.request.get(route);
        }
        return response;
    }

    /** Envia o arquivo pela tela e devolve a resposta do POST (com step-up se a API pedir OTP). */
    async function uploadViaUi(file) {
        for (let attempt = 0; attempt < 2; attempt += 1) {
            await page.goto("/imports/new");
            await expect(uploadHeading()).toBeVisible();
            await page.setInputFiles("#csv-file", file);
            const [response] = await Promise.all([
                page.waitForResponse((r) => r.url().endsWith("/api/v1/user-imports") && r.request().method() === "POST", { timeout: 120_000 }),
                page.getByRole("button", { name: "Enviar e conferir" }).click(),
            ]);
            if (response.status() !== 403) return response;
            await stepUp();
        }
        throw new Error("Upload recusado com 403 mesmo após o step-up de OTP");
    }

    /** Confirma o lote pelo botão da tela; devolve a resposta do POST /confirm. */
    async function confirmViaUi() {
        const [response] = await Promise.all([
            page.waitForResponse((r) => /\/api\/v1\/user-imports\/[0-9a-f-]+\/confirm$/.test(r.url()) && r.request().method() === "POST", { timeout: 60_000 }),
            page.getByRole("button", { name: /Confirmar e criar os acessos/ }).click(),
        ]);
        return response;
    }

    /** Reprocessa os erros pelo botão da tela; reprocessar é escrita e pede OTP recente, então faz step-up e repete no 403. */
    async function reprocessErrorsViaUi(batchId) {
        for (let attempt = 0; attempt < 2; attempt += 1) {
            await page.goto(`/imports/${batchId}`);
            const [response] = await Promise.all([
                page.waitForResponse((r) => r.url().endsWith(`/api/v1/user-imports/${batchId}/reprocess-errors`) && r.request().method() === "POST", { timeout: 60_000 }),
                page.getByRole("button", { name: /Reprocessar linhas com erro/ }).click(),
            ]);
            if (response.status() !== 403) return response;
            results.otpStepUps = (results.otpStepUps ?? 0) + 1;
            await stepUp();
        }
        throw new Error("Reprocessamento recusado com 403 mesmo após o step-up de OTP");
    }

    const batchOf = async (id) => (await api(`/api/v1/user-imports/${id}`)).json();

    /** Todas as linhas do lote (sem dado pessoal), paginando. */
    async function allLines(id) {
        const lines = [];
        for (let pageNumber = 1; ; pageNumber += 1) {
            const body = await (await api(`/api/v1/user-imports/${id}/lines?page=${pageNumber}&pageSize=100`)).json();
            lines.push(...body.items);
            if (lines.length >= body.totalCount || body.items.length === 0) return lines;
        }
    }

    function sampleMemory() {
        try {
            for (const [name, mib] of Object.entries(stack.memorySamples())) {
                results.memoryPeakMiB[name] = Math.max(results.memoryPeakMiB[name] ?? 0, Math.round(mib));
            }
        } catch (error) {
            log("falha ao amostrar memória:", error.message);
        }
    }

    const isFinished = (batch) => ["Completed", "CompletedWithErrors", "Cancelled"].includes(batch.status);

    async function waitFinished(id, { everyMs = 15 * SECOND, timeoutMs = 25 * 60_000, onTick } = {}) {
        const deadline = Date.now() + timeoutMs;
        let lastMemory = 0;
        for (;;) {
            const batch = await batchOf(id);
            const p = batch.progress;
            log(`lote ${id.slice(0, 8)} ${batch.status} criados=${p.created} existentes=${p.alreadyExists} erros=${p.error} pendentes=${p.pending}`);
            if (Date.now() - lastMemory > 45 * SECOND) {
                sampleMemory();
                lastMemory = Date.now();
            }
            if (onTick) await onTick(batch);
            if (isFinished(batch)) return batch;
            if (Date.now() > deadline) throw new Error(`Lote ${id} não terminou em ${timeoutMs / 60_000} min`);
            await svc.sleep(everyMs);
        }
    }

    test.beforeAll(async ({ browser }) => {
        await svc.createInvestigator(importer);
        await stack.createPlainUser(plain);
        await stack.createTapQueue(TAP);
        context = await browser.newContext({ locale: "pt-BR" });
        page = await context.newPage();
        sampleMemory();
        results.dlqAtStart = (await stack.queueDepth("cadastro.user-import-lines.dlq")).total;
        log(`run ${RUN}: arquivo grande ${big.dataRows} linhas, pequeno ${small.dataRows}, saída em ${OUT}`);
    });

    test.afterAll(async () => {
        save();
        await stack.deleteTapQueue(TAP).catch(() => undefined);
        await svc.deleteKeycloakUser(importer.login).catch(() => undefined);
        await svc.deleteKeycloakUser(plain.login).catch(() => undefined);
        await context?.close();
    });

    test("H1: permissões, recusas e ClamAV fail-closed", async ({ browser }) => {
        test.setTimeout(15 * 60_000);
        const step = (results.steps.h1 = {});

        // Sem a role user-importer: 403.
        const plainContext = await browser.newContext({ locale: "pt-BR" });
        const plainPage = await plainContext.newPage();
        await plainPage.goto("/profile");
        await plainPage.locator("#username").fill(plain.login);
        await plainPage.locator("#password").fill(plain.password);
        await plainPage.locator("#kc-login").click();
        await expect(plainPage.getByRole("heading", { level: 1, name: "Meu perfil" })).toBeVisible();
        step.noRoleStatus = (await plainPage.request.get("/api/v1/user-imports")).status();
        await plainContext.close();
        expect(step.noRoleStatus).toBe(403);

        // Com a role: login pela tela (OTP) e acesso à lista.
        await page.goto("/imports/new");
        await completeKeycloak(uploadHeading());
        const before = await (await api("/api/v1/user-imports")).json();
        step.batchesBefore = before.totalCount;

        // Cabeçalho inesperado: 4xx em Problem Details, sem lote.
        const header = await uploadViaUi(headerFile);
        step.invalidHeaderStatus = header.status();
        step.invalidHeaderCode = (await header.json().catch(() => ({}))).code ?? null;
        expect(header.status()).toBeGreaterThanOrEqual(400);
        expect(header.status()).toBeLessThan(500);

        // ClamAV fora do ar: recusa (fail-closed), sem lote.
        stack.docker(["stop", "-t", "5", "cadastro-clamav-1"]);
        const scan = await uploadViaUi(tinyFile);
        step.clamavDownStatus = scan.status();
        step.clamavDownCode = (await scan.json().catch(() => ({}))).code ?? null;
        stack.docker(["start", "cadastro-clamav-1"]);
        await stack.waitHealthy("cadastro-clamav-1", 6 * 60_000);
        expect(scan.status()).toBeGreaterThanOrEqual(500);

        const after = await (await api("/api/v1/user-imports")).json();
        step.batchesAfter = after.totalCount;
        expect(after.totalCount).toBe(before.totalCount);
        save();
    });

    test("Legado: usuários com e-mail igual ao de linhas do CSV (cenários da marcação)", async () => {
        test.setTimeout(10 * 60_000);
        // Dois candidatos únicos (e-mail exato de linhas do CSV) e um controle cujo e-mail não está no CSV. A ambiguidade
        // (dois logins com o mesmo e-mail) não é reproduzível na stack real: o Legado tem índice único em NormalizedEmail.
        const seeds = [
            { tag: "u", email: big.people[Math.floor(VALID * 0.02)].email, expectMarked: true },
            { tag: "v", email: big.people[Math.floor(VALID * 0.5)].email, expectMarked: true },
            { tag: "w", email: `controle.${RUN}@example.test`, expectMarked: false },
        ];
        const created = [];
        for (const seed of seeds) {
            const login = `e1leg.${RUN}.${seed.tag}`;
            const id = await svc.registerLegacyUser({ login, email: seed.email, password: strongPassword(), mobilePhone: `119${String(Date.now()).slice(-7)}${created.length}` });
            created.push({ ...seed, login, id });
        }
        results.steps.legado = { seeded: created.length };
        results.legadoSeeds = created;
        save();
    });

    test("(a)(b)(c) lote grande: upload, resumo, confirmação e processamento pela UI", async () => {
        test.setTimeout(45 * 60_000);
        const step = (results.steps.big = {});

        const upload = await uploadViaUi(bigFile);
        step.uploadStatus = upload.status();
        expect(upload.status()).toBe(201);
        const summary = await upload.json();
        bigBatchId = summary.batchId;
        step.summary = { valid: summary.validLines, invalid: summary.invalidLines, duplicates: summary.duplicateLines };
        await expect(page.getByRole("heading", { name: "Resumo do arquivo" })).toBeVisible();
        const counts = await page.locator("dl.counts").innerText();
        step.summaryOnScreen = counts.replace(/\s+/g, " ").trim();
        expect(summary.validLines).toBe(VALID);
        expect(summary.invalidLines).toBe(INVALID);
        expect(summary.duplicateLines).toBe(DUPLICATES);
        await page.screenshot({ path: path.join(OUT, "shot-resumo.png"), fullPage: true });

        // H1: nada foi publicado antes da confirmação.
        step.tapBeforeConfirm = (await stack.queueDepth(TAP)).total;
        expect(step.tapBeforeConfirm).toBe(0);

        const confirmedAt = Date.now();
        const confirm = await confirmViaUi();
        step.confirmStatus = confirm.status();
        expect(confirm.status()).toBeLessThan(300);
        bigConfirmedAt = confirmedAt;
        log(`lote ${bigBatchId} confirmado`);

        const finished = await waitFinished(bigBatchId, { everyMs: 20 * SECOND, timeoutMs: 30 * 60_000 });
        step.totalSeconds = Math.round((Date.now() - confirmedAt) / 1000);
        step.finalStatus = finished.status;
        step.progress = finished.progress;
        step.confirmedAtUtc = finished.confirmedAtUtc;
        step.finishedAtUtc = finished.finishedAtUtc;
        if (finished.confirmedAtUtc && finished.finishedAtUtc) {
            const secs = (Date.parse(finished.finishedAtUtc) - Date.parse(finished.confirmedAtUtc)) / 1000;
            step.processingSecondsServer = Math.round(secs);
            step.throughputPerSecond = Number((finished.progress.created / secs).toFixed(2));
        }
        await page.goto(`/imports/${bigBatchId}`);
        await expect(page.getByText("Concluído").first()).toBeVisible();
        await page.screenshot({ path: path.join(OUT, "shot-detalhe-grande.png"), fullPage: true });
        sampleMemory();
        save();
        expect(finished.progress.pending).toBe(0);
        expect(finished.progress.created + finished.progress.alreadyExists + finished.progress.error).toBe(VALID);
        expect(finished.progress.error).toBe(0);
    });

    test("(d) Keycloak e Mailpit: N usuários, sem senha, UPDATE_PASSWORD, N e-mails de 72 h", async () => {
        test.setTimeout(20 * 60_000);
        const step = (results.steps.keycloak = {});
        const bigEmails = new Set(big.people.map((p) => p.email));
        const users = (await stack.importedKeycloakUsers()).filter((u) => bigEmails.has(u.email));
        const byEmail = new Map(users.map((u) => [u.email, u]));
        step.usersWithImportLineId = users.length;
        step.distinctImportLineIds = new Set(users.map((u) => u.attributes.importLineId[0])).size;
        step.distinctEmails = byEmail.size;
        const missing = big.people.filter((p) => !byEmail.has(p.email));
        step.csvPeopleMissingInKeycloak = missing.length;
        const types = await stack.credentialTypes(users.map((u) => u.id));
        step.usersWithPassword = [...types.values()].filter((t) => t.includes("password")).length;
        step.usersWithoutUpdatePassword = users.filter((u) => !u.requiredActions?.includes("UPDATE_PASSWORD")).length;
        step.usersDisabled = users.filter((u) => !u.enabled).length;
        expect(users.length).toBe(VALID);
        expect(step.distinctImportLineIds).toBe(VALID);
        expect(step.csvPeopleMissingInKeycloak).toBe(0);
        expect(step.usersWithPassword).toBe(0);
        expect(step.usersWithoutUpdatePassword).toBe(0);

        // Mailpit: um e-mail por pessoa.
        const mails = (await stack.mailpitMessagesTo(`imp.${RUN}.`));
        const perRecipient = new Map();
        for (const m of mails) for (const t of m.To ?? []) perRecipient.set(t.Address, (perRecipient.get(t.Address) ?? 0) + 1);
        step.mailpitMessages = mails.length;
        step.mailpitRecipients = perRecipient.size;
        step.recipientsWithMoreThanOne = [...perRecipient.values()].filter((n) => n > 1).length;
        expect(mails.length).toBe(VALID);
        expect(step.recipientsWithMoreThanOne).toBe(0);

        // Validade do link: o token de ação do Keycloak carrega exp; deve ser ~72 h depois do envio.
        const sample = await stack.mailpit(`/message/${mails[Math.floor(mails.length / 2)].ID}`);
        const link = /https?:\/\/[^\s"<>]*action-token\?key=([\w.-]+)/.exec(`${sample.Text}\n${sample.HTML}`);
        expect(link, "link de ativação no e-mail").toBeTruthy();
        const payload = JSON.parse(Buffer.from(link[1].split(".")[1], "base64url").toString("utf8"));
        const sentAt = Date.parse(sample.Date) / 1000;
        step.activationLinkLifespanSeconds = Math.round(payload.exp - sentAt);
        step.activationLinkActions = payload.typ ?? payload.actions ?? null;
        expect(Math.abs(step.activationLinkLifespanSeconds - ACTIVATION_LIFESPAN_S)).toBeLessThan(300);
        save();
    });

    test("(i) marcação no Legado: só candidato único de e-mail exato", async () => {
        const step = (results.steps.legadoMark = {});
        const lines = await allLines(bigBatchId);
        results.steps.big.legacyMarkStatusCounts = lines.reduce((acc, l) => ({ ...acc, [l.legacyMarkStatus]: (acc[l.legacyMarkStatus] ?? 0) + 1 }), {});
        const rowEmail = (lineNumber) => big.rows[lineNumber - 1].split(",")[1];
        const kcUsers = new Map((await stack.importedKeycloakUsers()).map((x) => [x.email, x]));
        const rows = stack.legadoSql(`SELECT UserName, ISNULL(CONVERT(varchar(33),MigratedAt,126),'NULL'), ISNULL(CONVERT(varchar(40),KeycloakUserId),'NULL') FROM AspNetUsers WHERE UserName LIKE 'e1leg.${RUN}.%' ORDER BY UserName`);
        step.seeds = [];
        for (const seed of results.legadoSeeds) {
            const [, migratedAt, kcId] = rows.find(([name]) => name === seed.login);
            const line = lines.find((l) => rowEmail(l.lineNumber) === seed.email);
            const kcUser = kcUsers.get(seed.email);
            step.seeds.push({ tag: seed.tag, expectMarked: seed.expectMarked, migratedAtSet: migratedAt !== "NULL", keycloakUserIdMatches: kcUser ? kcId.toLowerCase() === kcUser.id.toLowerCase() : null, lineLegacyMarkStatus: line?.legacyMarkStatus ?? "(sem linha no CSV)" });
            expect(migratedAt !== "NULL", `marcação do login ${seed.tag}`).toBe(seed.expectMarked);
            if (seed.expectMarked) expect(kcId.toLowerCase()).toBe(kcUser.id.toLowerCase());
        }
        // Fora os candidatos únicos, nenhum outro login do Legado foi marcado por esta execução.
        step.legadoUsersMarkedByRun = Number(stack.legadoSql(`SELECT COUNT(*) FROM AspNetUsers WHERE KeycloakUserId IS NOT NULL AND Email LIKE '%${RUN}%'`)[0][0]);
        expect(step.legadoUsersMarkedByRun).toBe(results.legadoSeeds.filter((x) => x.expectMarked).length);
        save();
    });

    test("(e) reenvio do mesmo arquivo: 409 e ninguém novo", async () => {
        const step = (results.steps.reupload = {});
        const before = (await stack.importedKeycloakUsers()).length;
        const response = await uploadViaUi(bigFile);
        step.status = response.status();
        step.code = (await response.json().catch(() => ({}))).code ?? null;
        await expect(page.getByText("Ver o lote existente")).toBeVisible();
        const after = (await stack.importedKeycloakUsers()).length;
        step.keycloakUsersBefore = before;
        step.keycloakUsersAfter = after;
        expect(response.status()).toBe(409);
        expect(after).toBe(before);
        save();
    });

    test("(f) idempotência: mesma mensagem republicada tem efeito 1x", async () => {
        const step = (results.steps.idempotency = {});
        // Fila-espelho: todas as mensagens publicadas pelo lote grande (e, depois, pelo pequeno).
        const taps = await stack.drainQueue(TAP, 100);
        drainedTap.push(...taps);
        step.tapSampleCount = taps.length;
        expect(taps.length).toBeGreaterThan(0);
        const message = taps[0];
        const body = JSON.parse(message.payload);
        step.tapSampleKeys = Object.keys(body.data ?? body);
        const lines = await allLines(bigBatchId);
        const mailsBefore = (await stack.mailpitMessagesTo(`imp.${RUN}.`)).length;
        const usersBefore = (await stack.importedKeycloakUsers()).length;
        const dlqBefore = (await stack.queueDepth("cadastro.user-import-lines.dlq")).total;

        step.routed = await stack.republish(message);
        expect(step.routed).toBe(true);
        await svc.sleep(25 * SECOND);

        const lineAfter = (await allLines(bigBatchId)).find((l) => l.id === (body.data?.lineId ?? body.lineId));
        const lineBefore = lines.find((l) => l.id === lineAfter.id);
        step.lineBefore = lineBefore.status;
        step.lineAfter = lineAfter.status;
        step.mailsBefore = mailsBefore;
        step.mailsAfter = (await stack.mailpitMessagesTo(`imp.${RUN}.`)).length;
        step.keycloakUsersBefore = usersBefore;
        step.keycloakUsersAfter = (await stack.importedKeycloakUsers()).length;
        step.dlqBefore = dlqBefore;
        step.dlqAfter = (await stack.queueDepth("cadastro.user-import-lines.dlq")).total;
        step.mainQueueAfter = await stack.queueDepth("cadastro.user-import-lines");
        expect(step.lineAfter).toBe(step.lineBefore);
        expect(step.mailsAfter).toBe(mailsBefore);
        expect(step.keycloakUsersAfter).toBe(usersBefore);
        save();
    });

    test("(g) resiliência: Keycloak parado no meio de um lote, retry e conclusão sem perda", async () => {
        test.setTimeout(30 * 60_000);
        const step = (results.steps.resilience = {});
        const upload = await uploadViaUi(smallFile);
        expect(upload.status()).toBe(201);
        const summary = await upload.json();
        smallBatchId = summary.batchId;
        step.summary = { valid: summary.validLines, invalid: summary.invalidLines, duplicates: summary.duplicateLines };

        // Gatilho da falha definitiva real: o username que o importador usaria (imp.<24 hex do SHA-256 do id da linha>) já pertence a outro
        // usuário, sem o atributo importLineId e com outro e-mail; o Keycloak devolve 409 e o consumidor classifica como identity.rejected.
        const uploaded = await allLines(smallBatchId);
        for (const person of small.people.filter((p) => p.rejected)) {
            const fileLine = small.rows.findIndex((row) => row.includes(person.email)) + 1;
            const line = uploaded.find((l) => l.lineNumber === fileLine);
            expect(line, `linha ${fileLine} do arquivo (pessoa rejeitada ${person.index})`).toBeTruthy();
            const username = `imp.${createHash("sha256").update(line.id).digest("hex").slice(0, 24)}`;
            await stack.createPlainUser({ login: username, email: `bloqueio.${smallRun}.${person.index}@example.test`, password: strongPassword() });
        }

        const confirm = await confirmViaUi();
        expect(confirm.status()).toBeLessThan(300);

        // Espera algumas criações e derruba o Keycloak.
        let stopped = false;
        await waitFinished(smallBatchId, {
            everyMs: 2 * SECOND,
            onTick: async (batch) => {
                if (!stopped && batch.progress.created >= Math.min(40, Math.floor(SMALL_VALID / 4))) {
                    stopped = true;
                    step.createdWhenStopped = batch.progress.created;
                    log("parando o Keycloak");
                    stack.docker(["stop", "-t", "5", "keycloak-keycloak-1"]);
                    step.keycloakStoppedAt = new Date().toISOString();
                    throw Object.assign(new Error("stopped"), { stopped: true });
                }
            },
        }).catch((error) => {
            if (!error.stopped) throw error;
        });
        expect(stopped).toBe(true);

        // Observa a fila de retry enchendo e deixa passar pelo menos um ciclo de 30 s com o Keycloak fora.
        let retryPeak = 0;
        const stopDeadline = Date.now() + 90 * SECOND;
        const outageStart = Date.now();
        while (Date.now() < stopDeadline) {
            const depth = await stack.queueDepth("cadastro.user-import-lines.retry");
            retryPeak = Math.max(retryPeak, depth.total);
            if (retryPeak > 0 && Date.now() - outageStart > 35 * SECOND) break;
            await svc.sleep(2 * SECOND);
        }
        step.retryQueuePeak = retryPeak;
        step.outageSeconds = Math.round((Date.now() - outageStart) / 1000);
        stack.docker(["start", "keycloak-keycloak-1"]);
        await stack.waitHealthy("keycloak-keycloak-1", 5 * 60_000);
        step.keycloakBackAt = new Date().toISOString();
        expect(retryPeak).toBeGreaterThan(0);

        const finished = await waitFinished(smallBatchId, { everyMs: 10 * SECOND, timeoutMs: 15 * 60_000 });
        step.finalStatus = finished.status;
        step.progress = finished.progress;
        const createdExpected = SMALL_VALID - SMALL_REJECTED;
        expect(finished.progress.pending).toBe(0);
        expect(finished.progress.created).toBe(createdExpected);
        expect(finished.progress.error).toBe(SMALL_REJECTED);

        // Sem perda e sem duplicata: um usuário e um e-mail por pessoa criada.
        const emails = new Set(small.people.filter((p) => !p.rejected).map((p) => p.email));
        const users = (await stack.importedKeycloakUsers()).filter((u) => emails.has(u.email));
        step.keycloakUsers = users.length;
        step.distinctEmails = new Set(users.map((u) => u.email)).size;
        const mails = await stack.mailpitMessagesTo(`imp.${smallRun}.`);
        const perRecipient = new Map();
        for (const m of mails) for (const t of m.To ?? []) perRecipient.set(t.Address, (perRecipient.get(t.Address) ?? 0) + 1);
        step.mailpitMessages = mails.length;
        step.recipientsWithMoreThanOne = [...perRecipient.values()].filter((n) => n > 1).length;
        step.recipientsWithNoMail = [...emails].filter((e) => !perRecipient.has(e)).length;
        expect(users.length).toBe(createdExpected);
        expect(step.distinctEmails).toBe(createdExpected);

        // Falha definitiva: linha com erro e código, DLQ com as mensagens.
        const lines = await allLines(smallBatchId);
        const errors = lines.filter((l) => l.status === "Error");
        step.errorLines = errors.map((l) => ({ lineNumber: l.lineNumber, code: l.failureReason }));
        expect(errors.length).toBe(SMALL_REJECTED);
        expect(errors.every((l) => l.failureReason)).toBe(true);
        // As estatísticas do gerenciamento do RabbitMQ atrasam alguns segundos: espera o contador alcançar o esperado.
        step.dlq = await stack.queueDepth("cadastro.user-import-lines.dlq");
        for (let i = 0; i < 15 && step.dlq.total < SMALL_REJECTED; i += 1) {
            await svc.sleep(2 * SECOND);
            step.dlq = await stack.queueDepth("cadastro.user-import-lines.dlq");
        }
        expect(step.dlq.total).toBeGreaterThanOrEqual(SMALL_REJECTED);
        step.retryQueueAfter = await stack.queueDepth("cadastro.user-import-lines.retry");
        step.mainQueueAfter = await stack.queueDepth("cadastro.user-import-lines");
        save();

        // Duplicata observável: usuários e e-mails extras com a mesma pessoa.
        expect(step.recipientsWithMoreThanOne, "pessoas com mais de um e-mail de ativação").toBe(0);
    });

    test("H6: lista, detalhe, relatório CSV e reprocessar erros pela UI", async () => {
        const step = (results.steps.h6 = {});
        await page.goto("/imports");
        await expect(listHeading()).toBeVisible();
        await page.screenshot({ path: path.join(OUT, "shot-lista.png"), fullPage: true });
        await page.goto(`/imports/${smallBatchId}`);
        await expect(page.getByText("Concluído com erros").first()).toBeVisible();
        await page.screenshot({ path: path.join(OUT, "shot-detalhe-pequeno.png"), fullPage: true });

        const report = await (await api(`/api/v1/user-imports/${smallBatchId}/report`)).text();
        step.reportLines = report.trim().split("\n").length;
        step.reportHasPersonalData = /@example\.test|Sintetico/.test(report) || [...sensitive.cpfs].some((c) => report.includes(c));
        expect(step.reportHasPersonalData).toBe(false);

        expect((await reprocessErrorsViaUi(smallBatchId)).ok()).toBe(true);
        await expect(page.getByText(/Linhas com erro reenviadas para a fila de processamento: \d+\./)).toBeVisible();
        const finished = await waitFinished(smallBatchId, { everyMs: 5 * SECOND, timeoutMs: 10 * 60_000 });
        const lines = await allLines(smallBatchId);
        step.afterReprocess = { status: finished.status, errors: lines.filter((l) => l.status === "Error").length, reprocessCounts: lines.filter((l) => l.reprocessCount > 0).length };
        step.dlqAfterReprocess = await stack.queueDepth("cadastro.user-import-lines.dlq");
        expect(step.afterReprocess.errors).toBe(SMALL_REJECTED);
        expect(step.afterReprocess.reprocessCounts).toBe(SMALL_REJECTED);
        save();
    });

    test("(h) LGPD: nenhum e-mail, CPF ou nome do CSV em logs de containers nem em payloads AMQP", async () => {
        test.setTimeout(20 * 60_000);
        const step = (results.steps.lgpd = { containers: {}, amqp: {} });
        const containers = stack
            .docker(["ps", "-a", "--format", "{{.Names}}"])
            .trim()
            .split("\n")
            .filter((n) => /^(cadastro|legado|keycloak|rabbitmq)-/.test(n) && !n.includes("mailpit") && !n.includes("provisioner"));
        const cpfRegex = /\b\d{3}\.\d{3}\.\d{3}-\d{2}\b|\b\d{11}\b/g;
        const scan = (text) => {
            const hits = { runMarker: 0, cpf: 0, name: 0 };
            for (const line of text.split("\n")) {
                const lower = line.toLowerCase();
                if (sensitive.runMarkers.some((m) => lower.includes(m.toLowerCase()))) hits.runMarker += 1;
                if (line.includes(sensitive.nameMarker)) hits.name += 1;
                for (const match of line.match(cpfRegex) ?? []) if (sensitive.cpfs.has(cpfDigits(match))) hits.cpf += 1;
            }
            return hits;
        };
        // Controle positivo: o detector precisa achar marcador, CPF e nome plantados (senão "zero ocorrências" seria vazio).
        const control = scan(`x ${RUN} ${[...sensitive.cpfs][0]} Fulano ${sensitive.nameMarker}`);
        step.detectorControl = control;
        expect(control).toEqual({ runMarker: 1, cpf: 1, name: 1 });
        for (const name of containers) {
            const text = stack.containerLogs(name, startedAt);
            step.containers[name] = { bytes: text.length, lines: text.split("\n").length, ...scan(text) };
        }

        // Payloads AMQP: cópia de tudo o que o lote publicou (fila-espelho) e conteúdo de retry/DLQ/fila principal (sem consumir).
        const tapAll = [...drainedTap];
        for (;;) {
            const batch = await stack.drainQueue(TAP, 500);
            tapAll.push(...batch);
            if (batch.length < 500) break;
        }
        const tapPayloads = tapAll.map((m) => m.payload).join("\n");
        step.amqp.tapMessagesScanned = tapAll.length;
        step.amqp.tap = scan(tapPayloads);
        step.amqp.tapPayloadKeys = [...new Set(tapAll.flatMap((m) => Object.keys(JSON.parse(m.payload).data ?? {})))].sort();
        const dlqMessages = await stack.peekQueue("cadastro.user-import-lines.dlq", 100);
        step.amqp.dlqMessagesScanned = dlqMessages.length;
        step.amqp.dlq = scan(dlqMessages.map((m) => `${m.payload}\n${JSON.stringify(m.properties)}`).join("\n"));
        step.amqp.dlqSample = dlqMessages[0] ? { keys: Object.keys(JSON.parse(dlqMessages[0].payload).data ?? {}), headers: Object.keys(dlqMessages[0].properties?.headers ?? {}) } : null;

        // Esquema import: nenhuma coluna de texto com e-mail em claro.
        const plaintext = stack.legadoSql(
            "DECLARE @n int=0; DECLARE @sql nvarchar(max)=''; SELECT @sql=@sql+'SELECT @c=@c+COUNT(*) FROM import.['+t.name+'] WHERE ['+c.name+'] LIKE ''%@example.test%'';' FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE s.name='import' AND ty.name IN ('nvarchar','varchar'); EXEC sp_executesql @sql, N'@c int OUTPUT', @c=@n OUTPUT; SELECT @n;",
        );
        step.importSchemaPlaintextEmailRows = Number(plaintext.at(-1)?.[0] ?? -1);
        save();

        const containerHits = Object.entries(step.containers).flatMap(([name, h]) => (h.runMarker || h.cpf || h.name ? [`${name}: ${JSON.stringify(h)}`] : []));
        step.containerHits = containerHits;
        expect(step.amqp.tap).toEqual({ runMarker: 0, cpf: 0, name: 0 });
        expect(step.amqp.dlq).toEqual({ runMarker: 0, cpf: 0, name: 0 });
        expect(step.amqp.tapPayloadKeys).toEqual(["batchId", "lineId"]);
        expect(step.importSchemaPlaintextEmailRows).toBe(0);
        expect(containerHits, "ocorrências de dado do CSV em logs de containers").toEqual([]);
    });

    test("(j) métricas finais", async () => {
        const step = (results.steps.final = {});
        step.dlq = await stack.queueDepth("cadastro.user-import-lines.dlq");
        step.retry = await stack.queueDepth("cadastro.user-import-lines.retry");
        step.main = await stack.queueDepth("cadastro.user-import-lines");
        for (const [key, id] of [["big", bigBatchId], ["small", smallBatchId]]) {
            const batch = await batchOf(id);
            step[`${key}Batch`] = { id, status: batch.status, valid: batch.validLines, invalid: batch.invalidLines, duplicates: batch.duplicateLines, progress: batch.progress };
        }
        sampleMemory();
        results.finishedAt = new Date().toISOString();
        results.bigConfirmedAtMs = bigConfirmedAt;
        save();
        log("resultados em", path.join(OUT, "results.json"));
    });
});
