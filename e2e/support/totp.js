// Código TOTP (RFC 6238: HMAC-SHA1, 6 dígitos, passo de 30 s) para o investigador de teste.
const crypto = require("node:crypto");

/** Gera o código do passo atual; o segredo é a string ASCII importada no Keycloak. */
function totp(secret) {
    const counter = Buffer.alloc(8);
    counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30_000)));
    const digest = crypto.createHmac("sha1", Buffer.from(secret)).update(counter).digest();
    const offset = digest[19] & 15;
    return String((digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

module.exports = { totp };
