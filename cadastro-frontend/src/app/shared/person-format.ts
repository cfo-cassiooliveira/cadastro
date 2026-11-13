/** Celular em E.164 (+55DDNNNNNNNNN) para exibição: "(11) 91234-5678". Outro formato volta como veio. */
export function formatMobilePhone(e164: string): string {
  const match = /^\+55(\d{2})(\d{5})(\d{4})$/.exec(e164);
  return match ? `(${match[1]}) ${match[2]}-${match[3]}` : e164;
}

/** CEP com 8 dígitos para exibição: "01001-000". */
export function formatPostalCode(postalCode: string): string {
  return /^\d{8}$/.test(postalCode)
    ? `${postalCode.slice(0, 5)}-${postalCode.slice(5)}`
    : postalCode;
}

/** Nome para exibição: nome e sobrenome do Keycloak, ou o login quando não há nome. */
export function displayName(
  firstName: string | null,
  lastName: string | null,
  userName: string,
): string {
  const name = [firstName, lastName].filter((part) => !!part?.trim()).join(' ');
  return name || userName;
}

/** Iniciais do primeiro e do último nome, para o monograma (ex.: "Ana Paula Souza" → "AS"). */
export function initialsOf(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) {
    return '';
  }
  const last = parts.length > 1 ? parts[parts.length - 1][0] : '';
  return `${parts[0][0]}${last}`.toUpperCase();
}
