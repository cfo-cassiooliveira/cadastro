// LGPD nos logs: depois dos cenários de migração, o Graylog não pode ter login nem e-mail completos dos usuários de
// teste, e os eventos de tentativa inválida precisam ter o identificador mascarado (RN-10, RN-11, US-09).
const { test, expect } = require("@playwright/test");
const svc = require("../support/services");

test.skip(!svc.isMigrationStackConfigured(), "stack de migração não configurada (use scripts/e2e.sh)");

const startedAt = new Date(Date.now() - 60_000).toISOString();

test("Graylog não tem login nem e-mail completos e tem masked_login mascarado", async ({}, info) => {
    test.setTimeout(180_000);
    const users = svc.readTestUsers(info.project.name);
    expect(users.length).toBeGreaterThan(0);

    // Os eventos chegam ao Graylog com atraso: espera os de usuário inexistente (login e e-mail) aparecerem.
    let messages = [];
    const deadline = Date.now() + 90_000;
    for (;;) {
        messages = await svc.graylogMessagesSince(startedAt, "_exists_:masked_login");
        const masked = messages.map((m) => m.masked_login).filter(Boolean);
        if (masked.includes("e2e***") && masked.includes("e2e***@example")) break;
        if (Date.now() > deadline) break;
        await svc.sleep(5000);
    }
    // Margem para os últimos eventos do dispositivo serem indexados antes da varredura.
    await svc.sleep(10_000);
    messages = await svc.graylogMessagesSince(startedAt);

    const maskedLogins = new Set(messages.map((m) => m.masked_login).filter(Boolean));
    expect(maskedLogins.has("e2e***")).toBe(true);
    expect(maskedLogins.has("e2e***@example")).toBe(true);

    const leaks = [];
    for (const message of messages) {
        const text = JSON.stringify(message).toLowerCase();
        for (const { login, email } of users) {
            if (text.includes(login.toLowerCase()) || text.includes(email.toLowerCase())) {
                leaks.push(`${message.service_name ?? "?"}/${message.type ?? message.message?.slice(0, 40)}`);
            }
        }
        if (text.includes("@gmail.com")) leaks.push(`gmail em ${message.service_name ?? "?"}`);
    }
    expect(leaks).toEqual([]);
});
