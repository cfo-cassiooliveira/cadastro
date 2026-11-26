// Acesso à stack REAL pelo E2E da importação (E1): Keycloak Admin API, Mailpit, API de gerenciamento do RabbitMQ, Docker e
// SQL Server do Legado. Roda no HOST (precisa do CLI do Docker). Segredos são lidos dos .env irmãos e nunca impressos.
const { execFileSync } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");

const workspace = path.resolve(__dirname, "../../../..");

/** Lê uma variável de um .env sem imprimir o valor (remove CR e aspas externas). */
function readEnv(project, name) {
    const file = path.join(workspace, project, ".env");
    if (!fs.existsSync(file)) return undefined;
    const line = fs.readFileSync(file, "utf8").split(/\r?\n/).find((l) => l.startsWith(`${name}=`));
    if (!line) return undefined;
    return line.slice(name.length + 1).replace(/^'(.*)'$/, "$1").replace(/^"(.*)"$/, "$1");
}

const urls = {
    keycloak: process.env.E2E_KEYCLOAK_URL || "http://localhost:8180",
    mailpit: process.env.E2E_MAILPIT_URL || "http://127.0.0.1:8025",
    rabbit: process.env.E2E_RABBITMQ_URL || "http://127.0.0.1:15672",
    legado: process.env.E2E_LEGADO_URL || "http://localhost:4200",
};
const REALM = "workspace";
const VHOST = "dev";
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Executa o docker e devolve a saída (stdout+stderr). */
function docker(args, options = {}) {
    return execFileSync("docker", args, { encoding: "utf8", maxBuffer: 1024 * 1024 * 512, stdio: ["ignore", "pipe", "pipe"], ...options });
}

// ---------- Keycloak ----------
let cachedToken = { value: null, expiresAt: 0 };
async function kcToken() {
    if (cachedToken.value && Date.now() < cachedToken.expiresAt) return cachedToken.value;
    const secret = process.env.KC_AUTOMATION_CLIENT_SECRET || readEnv("keycloak", "KC_AUTOMATION_CLIENT_SECRET");
    const response = await fetch(`${urls.keycloak}/realms/master/protocol/openid-connect/token`, {
        method: "POST",
        body: new URLSearchParams({ grant_type: "client_credentials", client_id: "workspace-automation", client_secret: secret }),
    });
    if (!response.ok) throw new Error(`Token do Keycloak recusado (HTTP ${response.status})`);
    const json = await response.json();
    cachedToken = { value: json.access_token, expiresAt: Date.now() + (json.expires_in - 20) * 1000 };
    return cachedToken.value;
}

async function kc(method, route) {
    const response = await fetch(`${urls.keycloak}/admin/realms/${REALM}${route}`, { method, headers: { Authorization: `Bearer ${await kcToken()}` } });
    if (!response.ok) throw new Error(`Admin API ${method} ${route.split("?")[0]} falhou (HTTP ${response.status})`);
    return response.status === 204 ? null : response.json();
}

/** Todos os usuários criados pela importação (atributo importLineId), com a representação completa. */
async function importedKeycloakUsers() {
    const found = [];
    const pageSize = 500;
    for (let first = 0; ; first += pageSize) {
        const page = await kc("GET", `/users?first=${first}&max=${pageSize}&briefRepresentation=false`);
        for (const user of page) if (user.attributes?.importLineId?.[0]) found.push(user);
        if (page.length < pageSize) return found;
    }
}

/** Credenciais de cada usuário, com concorrência limitada; devolve {id -> tipos}. */
async function credentialTypes(userIds, concurrency = 16) {
    const result = new Map();
    let next = 0;
    await Promise.all(
        Array.from({ length: concurrency }, async () => {
            while (next < userIds.length) {
                const id = userIds[next++];
                result.set(id, (await kc("GET", `/users/${id}/credentials`)).map((c) => c.type));
            }
        }),
    );
    return result;
}

/** Cria um usuário de teste só com senha (sem role de importação), para provar o 403. */
async function createPlainUser({ login, email, password }) {
    const headers = { Authorization: `Bearer ${await kcToken()}`, "Content-Type": "application/json" };
    const created = await fetch(`${urls.keycloak}/admin/realms/${REALM}/users`, {
        method: "POST",
        headers,
        body: JSON.stringify({
            username: login, email, firstName: "Sem", lastName: "Permissao", emailVerified: true, enabled: true,
            credentials: [{ type: "password", value: password, temporary: false }],
        }),
    });
    if (created.status !== 201) throw new Error(`Criação do usuário sem role falhou (HTTP ${created.status})`);
}

// ---------- Mailpit ----------
async function mailpit(route, init) {
    const response = await fetch(`${urls.mailpit}/api/v1${route}`, init);
    if (!response.ok) throw new Error(`Mailpit ${route.split("?")[0]} falhou (HTTP ${response.status})`);
    return response.json();
}

/** Mensagens (resumo) cujo destinatário contém o texto, paginando. */
async function mailpitMessagesTo(fragment) {
    const all = [];
    const limit = 500;
    for (let start = 0; ; start += limit) {
        const page = await mailpit(`/search?query=${encodeURIComponent(`to:${fragment}`)}&start=${start}&limit=${limit}`);
        all.push(...page.messages);
        if (page.messages.length < limit) return all;
    }
}

// ---------- RabbitMQ (API de gerenciamento, usuário admin) ----------
function rabbitAuth() {
    const password = process.env.RABBITMQ_ADMIN_PASSWORD || readEnv("rabbitmq", "RABBITMQ_ADMIN_PASSWORD");
    return `Basic ${Buffer.from(`admin:${password}`).toString("base64")}`;
}
async function rabbit(method, route, body) {
    const response = await fetch(`${urls.rabbit}/api${route}`, {
        method,
        headers: { Authorization: rabbitAuth(), "Content-Type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok && response.status !== 404) throw new Error(`RabbitMQ ${method} ${route} falhou (HTTP ${response.status})`);
    if (response.status === 404) return null;
    const text = await response.text();
    return text ? JSON.parse(text) : null;
}
const queuePath = (name) => `/queues/${VHOST}/${encodeURIComponent(name)}`;
/** Mensagens prontas + não confirmadas da fila (0 se não existir). */
async function queueDepth(name) {
    const queue = await rabbit("GET", queuePath(name));
    return queue ? { ready: queue.messages_ready ?? 0, unacked: queue.messages_unacknowledged ?? 0, total: queue.messages ?? 0 } : { ready: 0, unacked: 0, total: 0 };
}

/** Fila descartável ligada ao exchange de eventos: guarda uma cópia de tudo o que a importação publica (para varrer LGPD). */
async function createTapQueue(name) {
    await rabbit("PUT", queuePath(name), { durable: true, auto_delete: false, arguments: { "x-queue-type": "classic" } });
    await rabbit("POST", `/bindings/${VHOST}/e/cadastro.events/q/${encodeURIComponent(name)}`, { routing_key: "cadastro.user-import-line.accepted.v1" });
}
async function deleteTapQueue(name) {
    await rabbit("DELETE", queuePath(name));
}
/** Lê (e descarta) até `count` mensagens da fila; ackmode=ack_requeue_false remove o que leu. */
async function drainQueue(name, count) {
    return (await rabbit("POST", `${queuePath(name)}/get`, { count, ackmode: "ack_requeue_false", encoding: "auto", truncate: 1_000_000 })) ?? [];
}
/** Lê sem remover (reentrega marcada como redelivered). */
async function peekQueue(name, count) {
    return (await rabbit("POST", `${queuePath(name)}/get`, { count, ackmode: "ack_requeue_true", encoding: "auto", truncate: 1_000_000 })) ?? [];
}
/** Publica no exchange de eventos com as mesmas propriedades (reentrega artificial, só no teste). */
async function republish(message, { routingKey = "cadastro.user-import-line.accepted.v1", payload = message.payload } = {}) {
    const result = await rabbit("POST", `/exchanges/${VHOST}/cadastro.events/publish`, {
        properties: message.properties, routing_key: routingKey, payload, payload_encoding: "string",
    });
    return result?.routed === true;
}

// ---------- Docker ----------
function containerLogs(name, sinceIso) {
    return docker(["logs", name, "--since", sinceIso], { maxBuffer: 1024 * 1024 * 1024 });
}
/** Uso de memória (MiB) de todos os containers de pé. */
function memorySamples() {
    const out = docker(["stats", "--no-stream", "--format", "{{.Name}}\t{{.MemUsage}}"]);
    const toMib = (text) => {
        const m = /([\d.]+)\s*(KiB|MiB|GiB)/.exec(text);
        return m ? Number(m[1]) * { KiB: 1 / 1024, MiB: 1, GiB: 1024 }[m[2]] : 0;
    };
    return Object.fromEntries(out.trim().split("\n").map((l) => l.split("\t")).map(([n, u]) => [n, toMib(u)]));
}
async function waitHealthy(container, timeoutMs = 300_000) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        const status = docker(["inspect", "-f", "{{.State.Health.Status}}", container]).trim();
        if (status === "healthy") return;
        await sleep(3000);
    }
    throw new Error(`${container} não ficou saudável em ${timeoutMs / 1000}s`);
}

// ---------- SQL Server do Legado ----------
/** Executa uma consulta como sa no SQL Server do Legado; devolve as linhas (colunas separadas por "|"). */
function legadoSql(query) {
    const password = readEnv("legado-cadastro-usuarios", "MSSQL_SA_PASSWORD");
    const sqlcmd = "/opt/mssql-tools18/bin/sqlcmd";
    const out = docker(
        ["exec", "-e", "SQLCMDPASSWORD", "legado-sqlserver-1", "sh", "-c",
            `S=${sqlcmd}; [ -x $S ] || S=$(ls /opt/mssql-tools*/bin/sqlcmd | head -1); $S -C -S localhost -U sa -d LegadoUsers -h -1 -W -s "|" -Q "SET NOCOUNT ON; ${query.replace(/"/g, '\\"')}"`],
        { env: { ...process.env, SQLCMDPASSWORD: password } },
    );
    if (/^Msg \d+/m.test(out)) throw new Error(`sqlcmd retornou erro: ${out.slice(0, 300)}`);
    return out.trim().split(/\r?\n/).filter(Boolean).map((l) => l.split("|"));
}

module.exports = {
    urls, REALM, VHOST, sleep, readEnv, docker,
    importedKeycloakUsers, credentialTypes, createPlainUser, kc,
    mailpit, mailpitMessagesTo,
    rabbit, queueDepth, createTapQueue, deleteTapQueue, drainQueue, peekQueue, republish,
    containerLogs, memorySamples, waitHealthy, legadoSql,
};
