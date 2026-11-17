import { LogSearchDataType } from './log-search.model';

/** Rótulos dos tipos, na ordem do seletor. */
export const DATA_TYPE_OPTIONS: readonly {
  readonly id: LogSearchDataType;
  readonly label: string;
}[] = [
  { id: 'cpf', label: $localize`CPF` },
  { id: 'cnpj', label: $localize`CNPJ` },
  { id: 'phone', label: $localize`Celular/telefone` },
  { id: 'email', label: $localize`E-mail` },
  { id: 'login', label: $localize`Login` },
];

/** Ajuda do campo "Valor" por tipo. */
export function valueHint(type: LogSearchDataType): string {
  switch (type) {
    case 'cpf':
    case 'cnpj':
      return $localize`Com ou sem pontuação.`;
    case 'phone':
      return $localize`Com DDD, com ou sem pontuação.`;
    case 'email':
      return $localize`O endereço completo, como o usuário o cadastrou.`;
    case 'login':
      return $localize`O login do usuário, sem espaços.`;
  }
}

const CASE_REFERENCE_PATTERN = /^[A-Za-z0-9._/-]{3,40}$/;
/** Mesmo limite do backend: 8 ou mais dígitos pode ser um CPF, CNPJ ou telefone digitado por engano. */
const MAX_CASE_REFERENCE_DIGITS = 8;
const CNPJ_PATTERN = /^[0-9A-Z]{12}[0-9]{2}$/;
const CPF_LENGTH = 11;
const PHONE_MIN_LENGTH = 10;
const PHONE_MAX_LENGTH = 11;
const PHONE_WITH_COUNTRY_MIN_LENGTH = 12;
const MIN_LOGIN_LENGTH = 4;

const onlyDigits = (raw: string): string => raw.replace(/\D/g, '');

/** Mensagem de erro do campo "Valor" para o tipo (espelha o canônico do backend); `null` se válido. */
export function validateValue(type: LogSearchDataType, raw: string): string | null {
  switch (type) {
    case 'cpf':
      return onlyDigits(raw).length === CPF_LENGTH
        ? null
        : $localize`Informe um CPF com 11 dígitos.`;
    case 'cnpj':
      return isCnpj(raw) ? null : $localize`Informe um CNPJ com 14 caracteres.`;
    case 'phone':
      return isPhone(raw) ? null : $localize`Informe o telefone com DDD.`;
    case 'email':
      return isEmail(raw) ? null : $localize`Informe um e-mail válido.`;
    case 'login':
      return raw.trim().length >= MIN_LOGIN_LENGTH ? null : $localize`Informe o login.`;
  }
}

/** Mensagem de erro da referência do caso (espelha o backend); `null` se válida. */
export function validateCaseReference(raw: string): string | null {
  if (raw.trim() === '') {
    return $localize`Informe a referência do caso.`;
  }
  if (!CASE_REFERENCE_PATTERN.test(raw)) {
    return $localize`Use de 3 a 40 caracteres: letras, números, ponto, hífen, barra ou sublinhado.`;
  }
  if (onlyDigits(raw).length >= MAX_CASE_REFERENCE_DIGITS) {
    return $localize`A referência tem 8 ou mais dígitos. Use o número do chamado (ex.: INC-1234), nunca um CPF, CNPJ ou telefone.`;
  }
  return null;
}

function isCnpj(raw: string): boolean {
  return CNPJ_PATTERN.test(raw.trim().replace(/[./-]/g, '').toUpperCase());
}

function isPhone(raw: string): boolean {
  let digits = onlyDigits(raw);
  if (digits.length >= PHONE_WITH_COUNTRY_MIN_LENGTH && digits.startsWith('55')) {
    digits = digits.slice(2);
  }
  return digits.length >= PHONE_MIN_LENGTH && digits.length <= PHONE_MAX_LENGTH;
}

function isEmail(raw: string): boolean {
  const normalized = raw.trim();
  return (
    normalized.length >= MIN_LOGIN_LENGTH && normalized.includes('@') && !normalized.startsWith('@')
  );
}
