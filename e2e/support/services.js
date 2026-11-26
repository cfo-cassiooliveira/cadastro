// Clientes HTTP dos serviços da stack usados pelo E2E de migração (Legado, Keycloak Admin, Mailpit e Graylog).
// Os endereços e os segredos chegam por variáveis de ambiente definidas pelo scripts/e2e.sh (nunca ficam no repositório).
const fs = require("node:fs");
const path = require("node:path");

const config = {
    legadoUrl: process.env.E2E_LEGADO_URL,
    keycloakUrl: process.env.E2E_KEYCLOAK_URL,
    mailpitUrl: process.env.E2E_MAILPIT_URL,
    graylogUrl: process.env.E2E_GRAYLOG_URL,
    keycloakAutomationSecret: process.env.KC_AUTOMATION_CLIENT_SECRET,
    graylogPassword: process.env.GRAYLOG_ROOT_PASSWORD,
    artifactsDir: process.env.E2E_ARTIFACTS_DIR || path.join(__dirname, "..", ".artifacts"),
};

const REALM = "workspace";
// Limite do Legado: 10 chamadas/min por IP nas rotas de credenciais (cadastro e login). Fica abaixo para sobrar folga.
const LEGADO_CALLS_PER_MINUTE = 8;
const ONE_MINUTE_MS = 60_000;

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Indica se todos os serviços necessários ao E2E de migração estão configurados. */
function isMigrationStackConfigured() {
    return Boolean(
        config.legadoUrl &&
        config.keycloakUrl &&
        config.mailpitUrl &&
        config.graylogUrl &&
        config.keycloakAutomationSecret &&
        config.graylogPassword,
    );
}

// ---------- Legado ----------

const legadoCallTimes = [];

/** Reserva uma vaga na janela do rate limit do Legado (vale também para logins feitos pela tela). */
async function reserveLegadoSlot() {
    for (;;) {
        const now = Date.now();
        while (legadoCallTimes.length > 0 && now - legadoCallTimes[0] > ONE_MINUTE_MS) legadoCallTimes.shift();
        if (legadoCallTimes.length < LEGADO_CALLS_PER_MINUTE) {
            legadoCallTimes.push(now);
            return;
        }
        await sleep(legadoCallTimes[0] + ONE_MINUTE_MS - now + 250);
    }
}

async function legadoPost(route, body) {
    for (let attempt = 0; attempt < 3; attempt += 1) {
        await reserveLegadoSlot();
        const response = await fetch(`${config.legadoUrl}/api/v1${route}`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
        });
        if (response.status !== 429) {
            return { status: response.status, text: await response.text() };
        }
        await sleep(ONE_MINUTE_MS);
    }
    throw new Error(`Legado manteve 429 em ${route}`);
}

/** Cadastra um usuário comum no Legado (autocadastro, perfil User) e devolve o id dele. */
async function registerLegacyUser({ login, email, password, mobilePhone = "11987654321" }) {
    const { status, text } = await legadoPost("/users", {
        fullName: "Teste Automatizado",
        userName: login,
        password,
        email,
        mobilePhone,
        postalCode: "01001000",
        street: "Praca da Se",
        number: "1",
        complement: "",
        district: "Se",
        cityIbgeCode: 3550308,
        state: "SP",
    });
    if (status !== 201) {
        throw new Error(`Cadastro no Legado falhou (HTTP ${status}): ${text.slice(0, 200)}`);
    }
    return JSON.parse(text).id;
}

/** Faz login no Legado pela API; devolve status e corpo (para checar o aviso de conta migrada). */
function legacyLogin(login, password) {
    return legadoPost("/auth/login", { userName: login, password });
}

/** Espera o Legado marcar a conta como migrada (o Keycloak confirma a marcação logo depois do login). */
async function waitUntilLegacyAccountIsMigrated(login, password, timeoutMs = 30_000) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
        const { text } = await legacyLogin(login, password);
        if (text.includes("auth.account_migrated")) return true;
        if (Date.now() > deadline) return false;
        await sleep(3000);
    }
}

// ---------- Keycloak Admin API ----------

let cachedToken = { value: null, expiresAt: 0 };

async function adminToken() {
    if (cachedToken.value && Date.now() < cachedToken.expiresAt) return cachedToken.value;
    const response = await fetch(`${config.keycloakUrl}/realms/master/protocol/openid-connect/token`, {
        method: "POST",
        body: new URLSearchParams({
            grant_type: "client_credentials",
            client_id: "workspace-automation",
            client_secret: config.keycloakAutomationSecret,
        }),
    });
    if (!response.ok) throw new Error(`Token da conta de serviço do Keycloak recusado (HTTP ${response.status})`);
    const json = await response.json();
    cachedToken = { value: json.access_token, expiresAt: Date.now() + (json.expires_in - 20) * 1000 };
    return cachedToken.value;
}

async function adminApi(method, route) {
    const response = await fetch(`${config.keycloakUrl}/admin/realms/${REALM}${route}`, {
        method,
        headers: { Authorization: `Bearer ${await adminToken()}` },
    });
    if (!response.ok) throw new Error(`Admin API ${method} ${route.split("?")[0]} falhou (HTTP ${response.status})`);
    return response.status === 204 ? null : response.json();
}

/** Busca o usuário do Keycloak pelo login exato; devolve null se não existir. */
async function findKeycloakUser(login) {
    const users = await adminApi("GET", `/users?username=${encodeURIComponent(login)}&exact=true`);
    return users[0] ?? null;
}

/** Credenciais (senha) que o usuário tem no Keycloak. */
function keycloakCredentials(userId) {
    return adminApi("GET", `/users/${userId}/credentials`);
}

/**
 * Cria no Keycloak um investigador de teste: role log-investigator e OTP (TOTP) já configurado com o segredo informado.
 * Devolve o id (sub) do usuário. A limpeza é feita por deleteKeycloakUser.
 */
async function createInvestigator({ login, email, password, otpSecret, role: roleName = "log-investigator" }) {
    const headers = { Authorization: `Bearer ${await adminToken()}`, "Content-Type": "application/json" };
    const base = `${config.keycloakUrl}/admin/realms/${REALM}`;
    const otp = {
        type: "otp",
        secretData: JSON.stringify({ value: otpSecret }),
        credentialData: JSON.stringify({ subType: "totp", digits: 6, counter: 0, period: 30, algorithm: "HmacSHA1" }),
    };
    const created = await fetch(`${base}/users`, {
        method: "POST",
        headers,
        body: JSON.stringify({
            username: login,
            email,
            firstName: "Investigador",
            lastName: "Teste",
            emailVerified: true,
            enabled: true,
            credentials: [{ type: "password", value: password, temporary: false }, otp],
        }),
    });
    if (created.status !== 201) throw new Error(`Criação do investigador falhou (HTTP ${created.status})`);
    const id = created.headers.get("location").split("/").pop();
    const role = await (await fetch(`${base}/roles/${roleName}`, { headers })).json();
    const mapped = await fetch(`${base}/users/${id}/role-mappings/realm`, {
        method: "POST",
        headers,
        body: JSON.stringify([role]),
    });
    if (!mapped.ok) throw new Error(`Atribuição da role ${roleName} falhou (HTTP ${mapped.status})`);
    return id;
}

/** Apaga o usuário de teste do Keycloak, se existir (limpeza). */
async function deleteKeycloakUser(login) {
    const user = await findKeycloakUser(login);
    if (user) await adminApi("DELETE", `/users/${user.id}`);
}

// ---------- Mailpit ----------

/** Mensagens recebidas pelo destinatário. */
async function mailsTo(address) {
    const response = await fetch(`${config.mailpitUrl}/api/v1/search?query=${encodeURIComponent(`to:${address}`)}`);
    return (await response.json()).messages ?? [];
}

/** Espera o e-mail chegar e devolve o texto dele. */
async function waitForMailText(address, timeoutMs = 30_000) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
        const [message] = await mailsTo(address);
        if (message) {
            return (await (await fetch(`${config.mailpitUrl}/api/v1/message/${message.ID}`)).json()).Text;
        }
        if (Date.now() > deadline) throw new Error("E-mail não chegou ao Mailpit no prazo");
        await sleep(1000);
    }
}

/** Remove do Mailpit os e-mails do destinatário (limpeza). */
async function deleteMailsTo(address) {
    await fetch(`${config.mailpitUrl}/api/v1/search?query=${encodeURIComponent(`to:${address}`)}`, {
        method: "DELETE",
    });
}

// ---------- Graylog ----------

async function graylogGet(route, params) {
    const url = new URL(`${config.graylogUrl}${route}`);
    url.search = new URLSearchParams(params).toString();
    const auth = Buffer.from(`admin:${config.graylogPassword}`).toString("base64");
    const response = await fetch(url, {
        headers: { Authorization: `Basic ${auth}`, "X-Requested-By": "e2e", Accept: "application/json" },
    });
    if (!response.ok) throw new Error(`Graylog ${route} falhou (HTTP ${response.status})`);
    return response.json();
}

/** Todas as mensagens (campos completos) recebidas desde o instante informado, com paginação. */
async function graylogMessagesSince(sinceIso, query = "*") {
    const pageSize = 500;
    const messages = [];
    // "to" fixo: mensagens novas durante a paginação deslocariam as páginas e fariam perder eventos.
    const to = new Date().toISOString();
    for (let offset = 0; ; offset += pageSize) {
        const page = await graylogGet("/api/search/universal/absolute", {
            query,
            from: sinceIso,
            to,
            limit: String(pageSize),
            offset: String(offset),
            fields: "*",
        });
        messages.push(...page.messages.map((entry) => entry.message));
        if (page.messages.length < pageSize) return messages;
    }
}

// ---------- Registro dos usuários de teste (para o teste de LGPD e a limpeza) ----------

const usersFile = () => path.join(config.artifactsDir, "test-users.jsonl");

function recordTestUser(entry) {
    fs.mkdirSync(config.artifactsDir, { recursive: true });
    fs.appendFileSync(usersFile(), `${JSON.stringify(entry)}\n`);
}

function readTestUsers(device) {
    if (!fs.existsSync(usersFile())) return [];
    return fs
        .readFileSync(usersFile(), "utf8")
        .split("\n")
        .filter(Boolean)
        .map((line) => JSON.parse(line))
        .filter((entry) => entry.device === device);
}

/** Registra um id do Legado cujas verificações o scripts/e2e.sh confere depois nos logs da API do Legado. */
function recordLegacyVerificationCheck(legacyUserId, expectedVerifications) {
    fs.mkdirSync(config.artifactsDir, { recursive: true });
    fs.appendFileSync(
        path.join(config.artifactsDir, "legacy-verification-checks.txt"),
        `${legacyUserId} ${expectedVerifications}\n`,
    );
}

module.exports = {
    config,
    sleep,
    isMigrationStackConfigured,
    reserveLegadoSlot,
    registerLegacyUser,
    legacyLogin,
    waitUntilLegacyAccountIsMigrated,
    findKeycloakUser,
    keycloakCredentials,
    deleteKeycloakUser,
    createInvestigator,
    mailsTo,
    waitForMailText,
    deleteMailsTo,
    graylogMessagesSince,
    recordTestUser,
    readTestUsers,
    recordLegacyVerificationCheck,
};
