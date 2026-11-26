// Gerador do CSV da importação real (E1): dados 100% FICTÍCIOS. Nomes sintéticos, e-mails em @example.test (domínio
// reservado, RFC 2606) únicos por execução e CPFs com dígitos verificadores válidos calculados por algoritmo.
const { createHash } = require("node:crypto");

const SYLLABLES = ["ba", "ce", "di", "fo", "gu", "ha", "jo", "ki", "lu", "ma", "ne", "pi", "ro", "su", "ta", "ve"];

/** Dígito verificador do CPF (módulo 11) para os dígitos informados. */
function cpfCheckDigit(digits) {
    const weightStart = digits.length + 1;
    const sum = digits.reduce((total, digit, index) => total + digit * (weightStart - index), 0);
    const rest = (sum * 10) % 11;
    return rest === 10 ? 0 : rest;
}

/** CPF válido (11 dígitos) derivado do índice e da semente; rejeita sequências de dígitos iguais. */
function validCpf(index, seed) {
    const hash = createHash("sha256").update(`${seed}:${index}`).digest();
    const base = Array.from({ length: 9 }, (_, i) => hash[i] % 10);
    // O índice entra nos dígitos (base 10) para garantir unicidade dentro do arquivo.
    const tag = String(index).padStart(5, "0").split("").map(Number);
    base.splice(4, 5, ...tag);
    if (base.every((digit) => digit === base[0])) base[0] = (base[0] + 1) % 10;
    const first = cpfCheckDigit(base);
    const second = cpfCheckDigit([...base, first]);
    return [...base, first, second].join("");
}

/** Nome sintético pronunciável e único por índice (sem relação com pessoa real). */
function syntheticName(index) {
    const part = (n) => {
        const a = SYLLABLES[n % 16];
        const b = SYLLABLES[Math.floor(n / 16) % 16];
        const c = SYLLABLES[Math.floor(n / 256) % 16];
        return `${a}${b}${c}`.replace(/^./, (letter) => letter.toUpperCase());
    };
    return `${part(index)} ${part(index * 7 + 3)} Sintetico`;
}

/** Formata o CPF com pontos e hífen (metade das linhas), para exercitar a normalização. */
function formatCpf(cpf, formatted) {
    return formatted ? `${cpf.slice(0, 3)}.${cpf.slice(3, 6)}.${cpf.slice(6, 9)}-${cpf.slice(9)}` : cpf;
}

/**
 * Monta o arquivo: `valid` linhas válidas e únicas, mais inválidas e duplicadas intercaladas, total de `valid+invalid+dup`
 * linhas de dados (o limite é 5.000). `rejectedByIdentity` são linhas válidas para o CSV mas com um primeiro nome de 105 caracteres, acima do limite de 100 do
 * User Profile do Keycloak (400 só na criação: falha definitiva, vai à DLQ). Caractere proibido no nome não serve mais: o upload já o recusa (`row.name_invalid`); elas contam entre as `valid`. Devolve o texto e a lista das pessoas válidas (para as verificações).
 */
function buildCsv({ runId, valid, invalid = 0, duplicates = 0, rejectedByIdentity = 0 }) {
    const people = Array.from({ length: valid }, (_, i) => {
        const index = i + 1;
        const rejected = i >= valid - rejectedByIdentity;
        const cpf = validCpf(index, runId);
        return {
            index,
            rejected,
            name: rejected ? `${"Longo".repeat(21)} ${syntheticName(index + 10)}` : syntheticName(index + 10),
            email: `imp.${runId}.${String(index).padStart(5, "0")}@example.test`,
            cpf,
            phone: index % 3 === 0 ? `119${String(80000000 + index).slice(-8)}` : "",
        };
    });
    const toLine = (p) => [p.name, p.email, formatCpf(p.cpf, p.index % 2 === 0), p.phone].join(",");

    const badRows = [];
    for (let i = 0; i < invalid; i += 1) {
        const base = people[i % people.length];
        const kind = i % 4;
        if (kind === 0) badRows.push(`${base.name},bad.${runId}.${i}@example.test,111.111.111-11,`); // CPF inválido
        else if (kind === 1) badRows.push(`${base.name},sem-arroba-${runId}-${i},${validCpf(90000 + i, runId)},`); // e-mail inválido
        else if (kind === 2) badRows.push(`,nome.vazio.${runId}.${i}@example.test,${validCpf(91000 + i, runId)},`); // nome ausente
        else badRows.push(`${base.name},cpf.vazio.${runId}.${i}@example.test,,`); // CPF ausente
    }
    // Duplicadas: repetem exatamente pessoas já presentes (mesmo e-mail e CPF).
    const dupRows = Array.from({ length: duplicates }, (_, i) => toLine(people[(i * 37) % people.length]));

    const rows = people.map(toLine);
    // Intercala inválidas e duplicadas ao longo do arquivo (depois da pessoa original, no caso das duplicadas).
    const extras = [...badRows, ...dupRows];
    const step = Math.max(1, Math.floor(rows.length / Math.max(1, extras.length)));
    const out = [];
    let extraIndex = 0;
    rows.forEach((row, i) => {
        out.push(row);
        if ((i + 1) % step === 0 && extraIndex < extras.length && i > 40) out.push(extras[extraIndex++]);
    });
    while (extraIndex < extras.length) out.push(extras[extraIndex++]);

    return { text: `nome,email,cpf,telefone\n${out.join("\n")}\n`, people, rows: out, dataRows: out.length };
}

module.exports = { buildCsv, validCpf, cpfCheckDigit };
