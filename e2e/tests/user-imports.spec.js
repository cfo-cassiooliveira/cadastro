// E2E das telas de importação em lote (F1 da spec 002 do rabbitmq): envio, resumo, confirmação, acompanhamento por
// polling, reprocessamento e lista. A sessão do BFF e a API são simuladas no navegador (support/import-api-mock.js),
// então o teste não depende de Keycloak, ClamAV, SQL Server nem RabbitMQ; o relógio é controlado para o polling de 3 s.
// Com E2E_SHOTS_DIR definido, cada estado também grava um screenshot (verificação visual da regra "tela perfeita").
const path = require("node:path");
const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;
const { installImportMocks, BATCH_ID } = require("../support/import-api-mock");

const CSV = { name: "lista.csv", mimeType: "text/csv", buffer: Buffer.from("nome,email,cpf,telefone\n") };

/** Falha o teste com violações sérias/críticas do axe e com rolagem horizontal; opcionalmente grava o screenshot. */
async function checkScreen(page, name, testInfo) {
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
    const blocking = results.violations
        .filter((v) => ["serious", "critical"].includes(v.impact))
        .map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
    expect(blocking, `axe em ${name}`).toEqual([]);

    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
    expect(overflow, `rolagem horizontal em ${name}`).toBe(false);

    expect(await brokenWords(page), `palavra partida em ${name}`).toEqual([]);
    // Menu aberto por engano cobriria o conteúdo no celular.
    await expect(page.locator("nav.nav--open"), `menu aberto em ${name}`).toHaveCount(0);

    if (process.env.E2E_SHOTS_DIR) {
        await page.screenshot({ path: path.join(process.env.E2E_SHOTS_DIR, `${testInfo.project.name}-${name}.png`), fullPage: true });
    }
}

/** Palavras partidas no meio, exceto na quebra natural após hífen (botão, rótulo ou texto em coluna estreita demais): cada palavra deve caber numa linha só. */
async function brokenWords(page) {
    return page.evaluate(() => {
        const found = [];
        const walker = document.createTreeWalker(document.querySelector("main"), NodeFilter.SHOW_TEXT);
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            const parent = node.parentElement;
            // O código de suporte e blocos de código podem quebrar de propósito (overflow-wrap: anywhere / rolagem própria).
            if (!parent || parent.closest(".data-table__code, pre, .visually-hidden, .table-scroll thead")) continue;
            const box = parent.getBoundingClientRect();
            if (box.width <= 1 || box.height <= 1) continue;
            for (const match of node.textContent.matchAll(/[^\s-]+/g)) {
                const range = document.createRange();
                range.setStart(node, match.index);
                range.setEnd(node, match.index + match[0].length);
                const tops = new Set([...range.getClientRects()].map((r) => Math.round(r.top / 4)));
                if (tops.size > 1) found.push(`${match[0]} (${parent.tagName.toLowerCase()}.${parent.className})`);
            }
        }
        return found;
    });
}

function collectConsoleErrors(page) {
    const errors = [];
    page.on("console", (message) => {
        if (message.type() === "error") errors.push(message.text());
    });
    page.on("pageerror", (error) => errors.push(error.message));
    return errors;
}

test.describe("Importação em lote (role user-importer)", () => {
    test("envia, confere o resumo, confirma, acompanha até concluir e reprocessa os erros", async ({ page }, testInfo) => {
        const errors = collectConsoleErrors(page);
        const mock = await installImportMocks(page);
        await page.clock.install();

        await page.goto("/imports/new");
        await expect(page.getByRole("heading", { level: 1 })).toHaveText("Enviar lista de pessoas");
        // No celular o menu começa recolhido.
        const menuToggle = page.getByRole("button", { name: "Menu" });
        const hasToggle = await menuToggle.isVisible();
        if (hasToggle) await menuToggle.click();
        await expect(page.getByRole("link", { name: "Importações" })).toBeVisible();
        if (hasToggle) await menuToggle.click();
        await checkScreen(page, "01-envio", testInfo);

        await page.getByRole("button", { name: "Enviar e conferir" }).click();
        await expect(page.getByText("Selecione um arquivo CSV.")).toBeVisible();
        await checkScreen(page, "02-envio-erro", testInfo);

        await page.setInputFiles("#csv-file", CSV);
        await page.getByRole("button", { name: "Enviar e conferir" }).click();
        await expect(page.getByRole("heading", { name: "Resumo do arquivo" })).toBeVisible();
        await expect(page.getByText("CPF inválido.")).toBeVisible();
        await checkScreen(page, "03-resumo", testInfo);

        await page.getByRole("button", { name: /Confirmar e criar os acessos/ }).click();
        await expect(page).toHaveURL(new RegExp(`/imports/${BATCH_ID}$`));
        await expect(page.getByText("Processando", { exact: true }).first()).toBeVisible();
        expect(mock.seen.posts.map((p) => p.csrf)).toEqual(["e2e-csrf", "e2e-csrf"]);
        await checkScreen(page, "04-processando", testInfo);

        // Polling: três ciclos de 3 s terminam o lote com uma linha com erro.
        for (let cycle = 0; cycle < 3; cycle += 1) await page.clock.fastForward(3000);
        await expect(page.getByText("Importação concluída com erros.")).toBeVisible();
        await expect(page.getByText("Concluído com erros").first()).toBeVisible();
        await checkScreen(page, "05-concluido-com-erros", testInfo);

        const pollsAfterCompletion = mock.seen.batchPolls;
        await page.clock.fastForward(9000);
        expect(mock.seen.batchPolls, "sem novas consultas depois de concluir").toBe(pollsAfterCompletion);

        const download = page.waitForEvent("download");
        await page.getByRole("button", { name: "Baixar relatório CSV" }).click();
        expect((await download).suggestedFilename()).toBe(`relatorio-lote-${BATCH_ID}.csv`);

        await page.getByRole("button", { name: /Reprocessar linhas com erro/ }).click();
        await expect(page.getByText("Linhas com erro reenviadas para a fila de processamento: 1.")).toBeVisible();
        await expect(page.getByText("Concluído", { exact: true }).first()).toBeVisible();
        await checkScreen(page, "06-concluido", testInfo);

        expect(errors).toEqual([]);
    });

    test("cancelar o resumo descarta o lote e volta ao formulário", async ({ page }, testInfo) => {
        await installImportMocks(page);
        await page.goto("/imports/new");
        await page.setInputFiles("#csv-file", CSV);
        await page.getByRole("button", { name: "Enviar e conferir" }).click();
        await page.getByRole("button", { name: "Cancelar lote" }).click();

        await expect(page.getByText("Lote cancelado. Nenhuma pessoa foi criada.")).toBeVisible();
        await expect(page.getByLabel("Arquivo CSV")).toBeVisible();
        await checkScreen(page, "07-cancelado", testInfo);
    });

    test("resumo em tela estreita: os rótulos dos contadores cabem nas caixas", async ({ page }) => {
        await installImportMocks(page);
        await page.goto("/imports/new");
        await page.setInputFiles("#csv-file", CSV);
        await page.getByRole("button", { name: "Enviar e conferir" }).click();
        await expect(page.getByRole("heading", { name: "Resumo do arquivo" })).toBeVisible();

        // 320 px é o mínimo de reflow (WCAG) e 360 px o Android comum: nenhum rótulo ou número pode vazar da caixa.
        for (const width of [320, 360]) {
            await page.setViewportSize({ width, height: 700 });
            const overflowing = await page.locator(".counts__item").evaluateAll((items) =>
                items.flatMap((item) => {
                    const box = item.getBoundingClientRect();
                    return [...item.children]
                        .filter((child) => {
                            const range = document.createRange();
                            range.selectNodeContents(child);
                            const text = range.getBoundingClientRect();
                            return text.left < box.left - 0.5 || text.right > box.right + 0.5;
                        })
                        .map((child) => child.textContent.trim());
                }),
            );
            expect(overflowing, `rótulos vazando em ${width}px`).toEqual([]);
        }
    });

    test("lista de lotes mostra estado e progresso", async ({ page }, testInfo) => {
        await installImportMocks(page);
        await page.goto("/imports");

        await expect(page.getByRole("heading", { level: 1 })).toHaveText("Importações em lote");
        await expect(page.getByRole("link", { name: /Lote de 25\/11\/2026/ })).toBeVisible();
        await checkScreen(page, "08-lista", testInfo);
    });
});

test("lote com cada tipo de falha de linha mostra mensagem em português, sem código como texto principal", async ({ page }, testInfo) => {
    await installImportMocks(page, { scenario: "errors" });
    await page.goto(`/imports/${BATCH_ID}`);

    await expect(page.getByText("O serviço de identidade estava indisponível. Reprocesse em alguns minutos.")).toBeVisible();
    await expect(page.getByText("O acesso foi criado, mas o e-mail de ativação não pôde ser enviado. Reprocesse.")).toBeVisible();
    await expect(page.getByText("Login do Legado não marcado: há mais de um com este e-mail.")).toBeVisible();
    // Código desconhecido cai no texto genérico; o código só aparece na linha pequena de suporte.
    await expect(page.getByText("Não foi possível criar o acesso. Tente reprocessar ou procure o suporte.")).toBeVisible();
    await expect(page.getByRole("button", { name: /Reprocessar a linha/ })).toHaveCount(10);
    await checkScreen(page, "09-falhas-por-tipo", testInfo);
});

test("sem a role user-importer: sem item de menu e rota leva a /forbidden", async ({ page }) => {
    await installImportMocks(page, { roles: ["user"] });
    await page.goto("/profile");
    await expect(page.getByRole("link", { name: "Importações" })).toHaveCount(0);

    await page.goto("/imports");
    await expect(page).toHaveURL(/\/forbidden$/);
    await expect(page.getByText("Você não tem permissão para acessar esta página.")).toBeVisible();
});
