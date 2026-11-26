import {
  describeLineFailure,
  hasLineFailureMessage,
  isLineReprocessable,
  KNOWN_LINE_FAILURE_CODES,
} from './user-import.messages';

describe('mensagens de falha de linha', () => {
  it.each(KNOWN_LINE_FAILURE_CODES)(
    'o código %s tem mensagem própria, sem cair no texto genérico',
    (code) => {
      expect(hasLineFailureMessage(code)).toBe(true);
      expect(describeLineFailure(code)).not.toBe(describeLineFailure('codigo.inexistente'));
      expect(describeLineFailure(code)).not.toContain(code);
    },
  );

  it('só personal_data.unavailable não admite reprocesso entre os códigos conhecidos', () => {
    const blocked = KNOWN_LINE_FAILURE_CODES.filter((code) => !isLineReprocessable(code));
    expect(blocked).toEqual(['personal_data.unavailable']);
    expect(isLineReprocessable('consumer.novo')).toBe(true);
    expect(isLineReprocessable(null)).toBe(true);
  });

  it('código desconhecido ou ausente usa o texto genérico, sem expor o código', () => {
    expect(describeLineFailure('consumer.novo')).toBe(
      'Não foi possível criar o acesso. Tente reprocessar ou procure o suporte.',
    );
    expect(describeLineFailure(null)).toBe(describeLineFailure('consumer.novo'));
  });
});
