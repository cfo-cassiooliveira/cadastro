import { displayName, formatMobilePhone, formatPostalCode, initialsOf } from './person-format';

describe('person-format', () => {
  it('formata celular E.164 no padrão brasileiro', () => {
    expect(formatMobilePhone('+5511912345678')).toBe('(11) 91234-5678');
    expect(formatMobilePhone('912345678')).toBe('912345678');
  });

  it('formata CEP com hífen', () => {
    expect(formatPostalCode('01001000')).toBe('01001-000');
    expect(formatPostalCode('0100')).toBe('0100');
  });

  it('usa nome e sobrenome, ou o login quando não há nome', () => {
    expect(displayName('Ana', 'Souza', 'ana.souza')).toBe('Ana Souza');
    expect(displayName('Ana', null, 'ana.souza')).toBe('Ana');
    expect(displayName(null, '  ', 'ana.souza')).toBe('ana.souza');
  });

  it('gera as iniciais do primeiro e do último nome', () => {
    expect(initialsOf('Ana Paula Souza')).toBe('AS');
    expect(initialsOf('ana.souza')).toBe('A');
    expect(initialsOf('   ')).toBe('');
  });
});
