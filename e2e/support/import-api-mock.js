// API da importação em lote simulada no navegador (page.route): permite exercitar as telas sem Keycloak, ClamAV, SQL
// Server nem RabbitMQ. O contrato espelha UserImportEndpoints (rotas, códigos e DTOs); a verdade do servidor é coberta
// pelos testes de integração do backend.
const BATCH_ID = "0b0e3a52-3f0a-4c0e-9d57-0f6c2a6a1111";
const SUMMARY_TOKEN = "e2e-summary-token";

const json = (route, status, body) =>
    route.fulfill({ status, contentType: "application/json", body: body === undefined ? "" : JSON.stringify(body) });

const session = (roles) => ({
    isAuthenticated: true,
    userName: "ana.souza",
    roles,
    antiforgeryToken: "e2e-csrf",
});

function lineFor(number, status, failureReason = null) {
    return { id: `00000000-0000-4000-8000-${String(number).padStart(12, "0")}`, lineNumber: number, status, failureReason, legacyMarkStatus: "NotAttempted", reprocessCount: 0 };
}

/**
 * Instala as rotas. `options.roles` define a sessão; `options.batches` o estado inicial dos lotes. Devolve um objeto com
 * os pedidos observados (para asserções) e o estado mutável do lote em acompanhamento.
 */
// Códigos de falha por linha que o backend emite (ImportFailureCodes) mais um desconhecido, para o cenário "errors".
const FAILURE_CODES = [
    "identity.unavailable",
    "identity.rejected",
    "identity.activation_email_rejected",
    "processing.retries_exhausted",
    "processing.failed",
    "message.invalid",
    "line.not_found",
    "personal_data.unavailable",
    "batch.invalid_state",
    "db.concurrency_conflict",
    "consumer.codigo_novo",
];

/** Lote concluído com erros: 3 linhas criadas (com marcação do Legado variada) e uma linha de erro por código. */
function errorScenario() {
    const marks = ["Ambiguous", "Failed", "NoCandidate"];
    const lines = marks.map((mark, i) => ({ ...lineFor(i + 1, "Created"), legacyMarkStatus: mark }));
    FAILURE_CODES.forEach((code, i) => lines.push(lineFor(marks.length + i + 1, "Error", code)));
    const batch = {
        id: BATCH_ID,
        status: "CompletedWithErrors",
        validLines: lines.length,
        invalidLines: 0,
        duplicateLines: 0,
        createdAtUtc: "2026-11-25T15:30:00Z",
        confirmedAtUtc: "2026-11-25T15:31:00Z",
        finishedAtUtc: "2026-11-25T15:40:00Z",
        progress: { pending: 0, created: marks.length, alreadyExists: 0, error: FAILURE_CODES.length, cancelled: 0 },
    };
    return { batch, lines };
}

async function installImportMocks(page, { roles = ["user", "user-importer"], processedPerPoll = 4, scenario = "flow" } = {}) {
    const seen = { posts: [], batchPolls: 0 };
    const state = { total: 10, created: 0, error: 0, confirmed: false, cancelled: false };

    const batch = () => {
        const pending = state.total - state.created - state.error;
        let status = "AwaitingConfirmation";
        if (state.cancelled) status = "Cancelled";
        else if (state.confirmed) status = pending > 0 ? "Processing" : state.error > 0 ? "CompletedWithErrors" : "Completed";
        return {
            id: BATCH_ID,
            status,
            validLines: state.total,
            invalidLines: 1,
            duplicateLines: 1,
            createdAtUtc: "2026-11-25T15:30:00Z",
            confirmedAtUtc: state.confirmed ? "2026-11-25T15:31:00Z" : null,
            finishedAtUtc: null,
            progress: state.confirmed ? { pending, created: state.created, alreadyExists: 0, error: state.error, cancelled: 0 } : { pending: 0, created: 0, alreadyExists: 0, error: 0, cancelled: state.cancelled ? state.total : 0 },
        };
    };

    await page.route("**/bff/user", (route) => json(route, 200, session(roles)));
    await page.route("**/api/v1/client-logs", (route) => json(route, 204));
    await page.route("**/api/v1/user-imports**", async (route) => {
        const request = route.request();
        const url = new URL(request.url());
        const path = url.pathname.replace("/api/v1/user-imports", "");
        const method = request.method();
        if (method === "POST") seen.posts.push({ path, csrf: request.headers()["x-xsrf-token"] });

        if (method === "POST" && path === "") {
            return json(route, 201, {
                batchId: BATCH_ID,
                validLines: state.total,
                invalidLines: 1,
                duplicateLines: 1,
                summaryToken: SUMMARY_TOKEN,
                issues: [
                    { line: 4, code: "row.cpf_invalid" },
                    { line: 7, code: "row.duplicate_email" },
                ],
                issuesTruncated: false,
            });
        }
        if (method === "POST" && path.endsWith("/confirm")) {
            const body = JSON.parse(request.postData() ?? "{}");
            if (body.summaryToken !== SUMMARY_TOKEN) return json(route, 400, { code: "import.confirmation_token_invalid" });
            state.confirmed = true;
            return json(route, 202, { messages: state.total });
        }
        if (method === "POST" && path.endsWith("/cancel")) {
            state.cancelled = true;
            return json(route, 204);
        }
        if (method === "POST" && path.endsWith("/reprocess-errors")) {
            const messages = state.error;
            // Reprocessar leva as linhas com erro direto a criadas, para o teste terminar o ciclo sem novo polling.
            state.created += messages;
            state.error = 0;
            return json(route, 202, { messages });
        }
        if (method === "GET" && path === "") {
            return json(route, 200, { items: [batch()], page: 1, pageSize: 20, totalCount: 1 });
        }
        if (scenario === "errors" && method === "GET" && path === `/${BATCH_ID}`) {
            return json(route, 200, errorScenario().batch);
        }
        if (scenario === "errors" && method === "GET" && path === `/${BATCH_ID}/lines`) {
            const { lines } = errorScenario();
            return json(route, 200, { items: lines, page: 1, pageSize: 20, totalCount: lines.length });
        }
        if (method === "GET" && path === `/${BATCH_ID}`) {
            seen.batchPolls += 1;
            // A cada consulta com o lote confirmado, algumas linhas terminam; a última deixa uma com erro.
            if (state.confirmed && state.created + state.error < state.total && seen.batchPolls > 1) {
                state.created = Math.min(state.total - 1, state.created + processedPerPoll);
                if (state.created === state.total - 1) { state.created = state.total - 1; state.error = 1; }
            }
            return json(route, 200, batch());
        }
        if (method === "GET" && path === `/${BATCH_ID}/lines`) {
            const items = [];
            for (let n = 1; n <= state.total; n += 1) {
                items.push(lineFor(n, n <= state.created ? "Created" : n === state.total && state.error ? "Error" : "Pending", n === state.total && state.error ? "keycloak.create_failed" : null));
            }
            return json(route, 200, { items, page: 1, pageSize: 20, totalCount: items.length });
        }
        if (method === "GET" && path === `/${BATCH_ID}/report`) {
            return route.fulfill({ status: 200, contentType: "text/csv; charset=utf-8", body: "linha,estado\n1,Created\n" });
        }
        return json(route, 404, { code: "import.not_found" });
    });
    return { seen, state, BATCH_ID };
}

module.exports = { installImportMocks, BATCH_ID, FAILURE_CODES };
