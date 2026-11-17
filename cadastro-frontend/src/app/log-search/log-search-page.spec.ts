import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthService } from '../auth/auth-service';
import { createLoggerSpy, LoggerSpy, provideLoggerSpy } from '../core/app-logger.testing';
import { LogSearchPage } from './log-search-page';

const URL = '/api/v1/log-search/queries';
// Valores fictícios só para o teste (CPF de exemplo com dígitos válidos).
const CPF = '123.456.789-09';
const RESULT = {
  dataType: 'cpf',
  tokens: [
    { keyId: 'k1', token: 'k1-00112233445566778899aabbccddeeff' },
    { keyId: 'k2', token: 'k2-ffeeddccbbaa99887766554433221100' },
  ],
  graylogQuery:
    'Cpf_token:("k1-00112233445566778899aabbccddeeff" OR "k2-ffeeddccbbaa99887766554433221100")',
};

describe('LogSearchPage', () => {
  let fixture: ComponentFixture<LogSearchPage>;
  let element: HTMLElement;
  let backend: HttpTestingController;
  let logger: LoggerSpy;
  const auth = {
    hasRole: vi.fn(),
    requestOtpStepUp: vi.fn(),
    antiforgeryToken: () => 'csrf',
  };

  beforeEach(async () => {
    logger = createLoggerSpy();
    auth.hasRole.mockReset().mockReturnValue(true);
    auth.requestOtpStepUp.mockReset();
    localStorage.clear();
    sessionStorage.clear();
    TestBed.configureTestingModule({
      imports: [LogSearchPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideLoggerSpy(logger),
        { provide: AuthService, useValue: auth },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LogSearchPage);
    element = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  afterEach(() => backend.verify());

  const field = (id: string) => element.querySelector<HTMLInputElement>(`#${id}`)!;
  const text = () => element.textContent ?? '';

  async function fill(value: string, reference: string): Promise<void> {
    field('search-value').value = value;
    field('search-value').dispatchEvent(new Event('input'));
    field('case-reference').value = reference;
    field('case-reference').dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  async function submit(): Promise<void> {
    element.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
  }

  async function reply(body: object | null, status: number): Promise<void> {
    backend.expectOne(URL).flush(body, { status, statusText: 'x' });
    // A resposta vira promise no serviço: deixa a continuação do componente rodar antes de conferir o DOM.
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
  }

  it('estado inicial: título, dica e formulário na ordem tipo, valor, referência, botão', () => {
    expect(element.querySelector('h1')?.textContent).toBe('Pesquisa em logs');
    expect(text()).toContain(
      'Informe o dado da pessoa e o motivo. O valor não é registrado; só o token e o motivo.',
    );
    const order = [...element.querySelectorAll('select, input, button[type="submit"]')].map(
      (control) => control.id || control.getAttribute('type'),
    );
    expect(order).toEqual(['data-type', 'search-value', 'case-reference', 'submit']);
    expect(field('search-value').getAttribute('autocomplete')).toBe('off');
    expect(element.querySelector('button[type="submit"]')?.textContent).toContain('Gerar consulta');
    expect(element.querySelector('pre')).toBeNull();
  });

  it('ignora um tipo fora da lista', async () => {
    const select = field('data-type') as unknown as HTMLSelectElement;
    select.dispatchEvent(new Event('change'));
    select.insertAdjacentHTML('beforeend', '<option value="xyz">x</option>');
    select.value = 'xyz';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(element.querySelector('#value-hint')?.textContent).toContain('Com ou sem pontuação.');
  });

  it('a ajuda do valor acompanha o tipo', async () => {
    const select = field('data-type') as unknown as HTMLSelectElement;
    select.value = 'phone';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(element.querySelector('#value-hint')?.textContent).toContain('Com DDD');
  });

  it('valor inválido: mostra a mensagem do campo, foca o campo e não chama a API', async () => {
    await fill('123', 'INC-1234');
    await submit();

    const error = element.querySelector('#value-error');
    expect(error?.textContent).toBe('Informe um CPF com 11 dígitos.');
    expect(error?.getAttribute('role')).toBe('alert');
    expect(field('search-value').getAttribute('aria-invalid')).toBe('true');
    expect(field('search-value').getAttribute('aria-describedby')).toContain('value-error');
    expect(document.activeElement).toBe(field('search-value'));
    backend.expectNone(URL);
  });

  it('referência sem texto ou com 8+ dígitos: mensagem clara e foco nela', async () => {
    await fill(CPF, '');
    await submit();
    expect(element.querySelector('#reference-error')?.textContent).toBe(
      'Informe a referência do caso.',
    );
    expect(document.activeElement).toBe(field('case-reference'));

    await fill(CPF, 'INC-12345678');
    await submit();
    expect(element.querySelector('#reference-error')?.textContent).toContain('8 ou mais dígitos');
    backend.expectNone(URL);
  });

  it('carregando: botão desabilitado com rótulo, campos bloqueados e status anunciado', async () => {
    await fill(CPF, 'INC-1234');
    await submit();

    expect(element.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(true);
    expect(element.querySelector('fieldset')?.disabled).toBe(true);
    expect(element.querySelector('[role="status"]')?.textContent).toContain('Gerando a consulta');
    expect(element.querySelector('form')?.getAttribute('aria-busy')).toBe('true');
    await reply(RESULT, 200);
  });

  it('envia o valor só no corpo, com o tipo e a referência', async () => {
    await fill(CPF, ' INC-1234 ');
    await submit();

    const request = backend.expectOne(URL);
    expect(request.request.method).toBe('POST');
    expect(request.request.url).not.toContain('123');
    expect(request.request.body).toEqual({
      dataType: 'cpf',
      value: CPF,
      caseReference: 'INC-1234',
    });
    request.flush(RESULT);
    await fixture.whenStable();
  });

  describe('sucesso', () => {
    beforeEach(async () => {
      await fill(CPF, 'INC-1234');
      await submit();
      await reply(RESULT, 200);
    });

    it('mostra a consulta, as chaves e o aviso, e foca o título do resultado', () => {
      expect(element.querySelector('pre')?.textContent).toBe(RESULT.graylogQuery);
      expect([...element.querySelectorAll('.keys__item')].map((item) => item.textContent)).toEqual([
        'k1',
        'k2',
      ]);
      expect(text()).toContain(
        'A consulta encontra só eventos com o campo *_token (legado e cadastro).',
      );
      expect(document.activeElement?.id).toBe('result-title');
    });

    it('apaga o valor do formulário, mas mantém a referência do caso', () => {
      expect(field('search-value').value).toBe('');
      expect(field('case-reference').value).toBe('INC-1234');
      expect(document.body.textContent).not.toContain(CPF);
    });

    it('nada vai para URL, localStorage, sessionStorage, AppLogger nem console', async () => {
      const consoleSpy = vi.spyOn(console, 'log');
      expect(location.href).not.toContain('123');
      expect(JSON.stringify(localStorage)).not.toContain('123');
      expect(JSON.stringify(sessionStorage)).not.toContain('123');
      const logged = JSON.stringify([
        logger.error.mock.calls,
        logger.warning.mock.calls,
        logger.info.mock.calls,
        consoleSpy.mock.calls,
      ]);
      expect(logged).not.toContain('123');
      consoleSpy.mockRestore();
    });

    it('"Copiar consulta" copia e confirma com "Consulta copiada."', async () => {
      const writeText = vi.fn().mockResolvedValue(undefined);
      vi.stubGlobal('navigator', { clipboard: { writeText } });
      [...element.querySelectorAll('button')]
        .find((button) => button.textContent?.includes('Copiar consulta'))
        ?.click();
      await fixture.whenStable();

      expect(writeText).toHaveBeenCalledWith(RESULT.graylogQuery);
      expect(element.querySelector('.result__copy-status')?.textContent).toContain(
        'Consulta copiada.',
      );
      vi.unstubAllGlobals();
    });

    it('avisa quando o navegador não deixa copiar', async () => {
      vi.stubGlobal('navigator', {
        clipboard: { writeText: vi.fn().mockRejectedValue(new Error('negado')) },
      });
      [...element.querySelectorAll('button')]
        .find((button) => button.textContent?.includes('Copiar consulta'))
        ?.click();
      await fixture.whenStable();

      expect(element.querySelector('.result__copy-status')?.textContent).toContain(
        'Não foi possível copiar',
      );
      vi.unstubAllGlobals();
    });

    it('"Nova pesquisa" fecha o resultado e foca o valor', async () => {
      [...element.querySelectorAll('button')]
        .find((button) => button.textContent?.includes('Nova pesquisa'))
        ?.click();
      await fixture.whenStable();

      expect(element.querySelector('pre')).toBeNull();
      expect(document.activeElement).toBe(field('search-value'));
    });
  });

  describe('erros', () => {
    beforeEach(async () => {
      await fill(CPF, 'INC-1234');
      await submit();
    });

    it('400 por campo: mensagem do campo, sem eco do valor, dados preservados', async () => {
      await reply({ errors: { value: ['log_search.invalid_value'] } }, 400);

      // O servidor recusou algo que o navegador achava válido: mensagem genérica, sem repetir o valor.
      expect(element.querySelector('#value-error')?.textContent).toBe('Revise o valor informado.');
      expect(text()).not.toContain(CPF);
      expect(field('search-value').value).toBe(CPF);
    });

    it('falha geral: mensagem, "Tentar de novo" com dados preservados e log só com o status', async () => {
      await reply(null, 500);

      expect(element.querySelector('[role="alert"]')?.textContent).toContain(
        'Não foi possível gerar a consulta agora. Tente novamente em instantes.',
      );
      expect(field('search-value').value).toBe(CPF);
      expect(logger.error).toHaveBeenCalledWith('log_search.failed', {
        source: 'log-search',
        status: 500,
      });
      expect(JSON.stringify(logger.error.mock.calls)).not.toContain('123');

      [...element.querySelectorAll('button')]
        .find((button) => button.textContent?.includes('Tentar de novo'))
        ?.click();
      await fixture.whenStable();
      const retried = backend.expectOne(URL);
      expect(retried.request.body.value).toBe(CPF);
      retried.flush(RESULT);
      await new Promise((resolve) => setTimeout(resolve));
      await fixture.whenStable();
      expect(element.querySelector('pre')).not.toBeNull();
    });

    it('429: mensagem de limite', async () => {
      await reply(null, 429);

      expect(element.querySelector('[role="alert"]')?.textContent).toContain(
        'Limite de pesquisas atingido. Tente novamente mais tarde.',
      );
    });

    it('403 com a role: pede o step-up de OTP e o botão abre o login com OTP', async () => {
      await reply(null, 403);

      expect(element.querySelector('[role="alert"]')?.textContent).toContain(
        'Confirme sua identidade com o código do autenticador.',
      );
      let valueAtNavigation: string | undefined;
      auth.requestOtpStepUp.mockImplementation(() => {
        valueAtNavigation = field('search-value').value + field('case-reference').value;
      });
      element.querySelector<HTMLButtonElement>('[role="alert"] button')?.click();
      expect(auth.requestOtpStepUp).toHaveBeenCalledWith('/log-search');
      // O valor já saiu do campo quando a navegação começa.
      expect(valueAtNavigation).toBe('');
    });

    it('403 sem a role: vai para o acesso negado', async () => {
      auth.hasRole.mockReturnValue(false);
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

      await reply(null, 403);

      expect(navigate).toHaveBeenCalledWith('/forbidden');
    });

    it('depois de um erro, uma nova tentativa limpa o aviso', async () => {
      await reply(null, 429);
      await submit();
      await reply(RESULT, 200);

      expect(element.querySelector('.log-search__problem')).toBeNull();
    });
  });
});
