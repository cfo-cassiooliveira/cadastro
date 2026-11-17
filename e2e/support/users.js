// Geração de usuários de teste: logins únicos por execução e senhas geradas em tempo de execução
// (nada fixo no repositório).
const { randomBytes } = require("node:crypto");

const runId = Date.now().toString(36);

/** Senha que atende à política do realm (12+, maiúscula, minúscula, número e símbolo). */
function strongPassword() {
    return `Aa1!${randomBytes(9).toString("base64url")}`;
}

/** Senha aceita pelo Legado (9 caracteres) mas fora da política do realm (12+). */
function shortPassword() {
    return `Aa1-${randomBytes(3).toString("hex")}x`.slice(0, 9);
}

/** Usuário descartável do Legado: login 4–30 caracteres [a-z0-9._-]; o e-mail usa o domínio reservado .test. */
function newTestUser(tag, deviceName, password = strongPassword()) {
    const login = `e2e.${runId}.${tag}${deviceName[0]}`;
    return { login, email: `${login}@example.test`, password, device: deviceName };
}

module.exports = { newTestUser, strongPassword, shortPassword };
