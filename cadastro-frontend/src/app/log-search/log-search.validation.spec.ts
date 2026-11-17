import { validateCaseReference, validateValue } from './log-search.validation';

describe('validateValue', () => {
  it.each([
    ['cpf', '123.456.789-09'],
    ['cpf', '12345678909'],
    ['cnpj', '12.ABC.345/01DE-35'],
    ['cnpj', '11222333000181'],
    ['phone', '(11) 91234-5678'],
    ['phone', '+55 11 91234-5678'],
    ['phone', '1132345678'],
    ['email', 'ana.souza@gmail.com'],
    ['login', 'ana.souza'],
  ] as const)('aceita %s "%s"', (type, value) => {
    expect(validateValue(type, value)).toBeNull();
  });

  it.each([
    ['cpf', '123', 'Informe um CPF com 11 dígitos.'],
    ['cnpj', '123', 'Informe um CNPJ com 14 caracteres.'],
    ['phone', '99999', 'Informe o telefone com DDD.'],
    ['email', 'sem-arroba', 'Informe um e-mail válido.'],
    ['email', '@gmail.com', 'Informe um e-mail válido.'],
    ['login', 'ab', 'Informe o login.'],
  ] as const)('recusa %s "%s" com a mensagem da spec', (type, value, message) => {
    expect(validateValue(type, value)).toBe(message);
  });
});

describe('validateCaseReference', () => {
  it.each(['INC-1234', 'CHG/2026.17', 'abc'])('aceita "%s"', (value) => {
    expect(validateCaseReference(value)).toBeNull();
  });

  it('exige a referência', () => {
    expect(validateCaseReference('  ')).toBe('Informe a referência do caso.');
  });

  it.each(['ab', 'INC 1234', 'a'.repeat(41), 'INC#1'])('recusa formato inválido "%s"', (value) => {
    expect(validateCaseReference(value)).toContain('Use de 3 a 40 caracteres');
  });

  it.each(['123.456.789-09', 'INC-12345678', '11 91234 5678'])(
    'recusa 8 ou mais dígitos em "%s" sem repetir o valor',
    (value) => {
      const message = validateCaseReference(value) ?? validateCaseReference('x');
      expect(message).not.toContain(value);
    },
  );

  it('recusa 8 ou mais dígitos com a explicação', () => {
    expect(validateCaseReference('INC-12345678')).toContain('8 ou mais dígitos');
    expect(validateCaseReference('INC-1234567')).toBeNull();
  });
});
