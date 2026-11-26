import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth-service';
import { createLoggerSpy, LoggerSpy, provideLoggerSpy } from '../core/app-logger.testing';
import {
  nextPollDelay,
  POLL_INTERVAL_MS,
  POLL_MAX_INTERVAL_MS,
  UserImportDetailPage,
} from './user-import-detail-page';

const ID = '0b0e3a52-3f0a-4c0e-9d57-0f6c2a6a1111';
const BATCH_URL = `/api/v1/user-imports/${ID}`;

function batch(status: string, progress: Partial<Record<string, number>> = {}) {
  return {
    id: ID,
    status,
    validLines: 10,
    invalidLines: 0,
    duplicateLines: 0,
    createdAtUtc: '2026-11-25T15:30:00Z',
    confirmedAtUtc: null,
    finishedAtUtc: null,
    progress: { pending: 0, created: 0, alreadyExists: 0, error: 0, cancelled: 0, ...progress },
  };
}

function line(id: string, number: number, status: string, failureReason: string | null = null) {
  return {
    id,
    lineNumber: number,
    status,
    failureReason,
    legacyMarkStatus: 'NotAttempted',
    reprocessCount: 0,
  };
}

const page = (items: object[], totalCount = items.length) => ({
  items,
  page: 1,
  pageSize: 20,
  totalCount,
});

describe('UserImportDetailPage', () => {
  let fixture: ComponentFixture<UserImportDetailPage>;
  let element: HTMLElement;
  let backend: HttpTestingController;
  let logger: LoggerSpy;
  const auth = { requestOtpStepUp: vi.fn() };

  beforeEach(() => {
    logger = createLoggerSpy();
    auth.requestOtpStepUp.mockReset();
    TestBed.configureTestingModule({
      imports: [UserImportDetailPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideLoggerSpy(logger),
        { provide: AuthService, useValue: auth },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(UserImportDetailPage);
    fixture.componentRef.setInput('id', ID);
    element = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => {
    vi.useRealTimers();
    backend.verify();
  });

  const text = () => element.textContent ?? '';
  const button = (label: string) =>
    [...element.querySelectorAll('button')].find((b) => b.textContent?.includes(label))!;
  const linesRequest = () => backend.expectOne((r) => r.url === `${BATCH_URL}/lines`);

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
  }

  async function load(batchBody: object, lines: object | null = null): Promise<void> {
    await fixture.whenStable();
    backend.expectOne(BATCH_URL).flush(batchBody);
    await settle();
    if (lines) {
      linesRequest().flush(lines);
      await settle();
    }
  }

  it('carregando: mostra o esqueleto com aviso acessível', async () => {
    await fixture.whenStable();

    expect(text()).toContain('Carregando o lote…');
    backend.expectOne(BATCH_URL).flush(batch('Cancelled'));
    await settle();
  });

  it('lote concluído: progresso, linhas sem dado pessoal e motivo por código', async () => {
    await load(
      batch('CompletedWithErrors', { created: 8, error: 2 }),
      page([line('L1', 1, 'Created'), line('L2', 2, 'Error', 'keycloak.create_failed')]),
    );

    expect(text()).toContain('10 de 10 linhas processadas');
    expect(text()).toContain('Concluído com erros');
    const rows = [...element.querySelectorAll('tbody tr')].map((r) =>
      r.textContent?.replace(/\s+/g, ' ').trim(),
    );
    expect(rows[0]).toContain('Criado');
    expect(rows[1]).toContain('Com erro');
    expect(rows[1]).toContain(
      'O acesso não pôde ser criado no serviço de identidade. Tente reprocessar.',
    );
    expect(rows[1]).toContain('Código para o suporte: keycloak.create_failed');
    expect(text()).not.toMatch(/undefined|null|NaN/);
  });

  it('código de falha desconhecido usa a mensagem genérica, sem o código como texto principal', async () => {
    await load(
      batch('CompletedWithErrors', { created: 9, error: 1 }),
      page([line('L2', 2, 'Error', 'consumer.novo_codigo')]),
    );

    const reason = element.querySelector('tbody .data-table__reason')!;
    expect(reason.firstChild?.textContent?.trim()).toBe(
      'Não foi possível criar o acesso. Tente reprocessar ou procure o suporte.',
    );
    expect(reason.querySelector('small')?.textContent).toContain('consumer.novo_codigo');
  });

  it('linha com dados apagados (personal_data.unavailable) não oferece reprocessar; as demais oferecem', async () => {
    await load(
      batch('CompletedWithErrors', { created: 8, error: 2 }),
      page([
        line('L1', 1, 'Error', 'personal_data.unavailable'),
        line('L2', 2, 'Error', 'identity.unavailable'),
      ]),
    );

    const rows = [...element.querySelectorAll('tbody tr')];
    expect(rows[0].querySelector('button')).toBeNull();
    expect(rows[1].querySelector('button')?.getAttribute('aria-label')).toBe(
      'Reprocessar a linha 2',
    );
  });

  it('reprocessar erros sem nenhuma linha reaberta explica que os dados não estão mais disponíveis', async () => {
    await load(
      batch('CompletedWithErrors', { created: 9, error: 1 }),
      page([line('L1', 1, 'Error', 'personal_data.unavailable')]),
    );

    button('Reprocessar linhas com erro').click();
    backend
      .expectOne(`${BATCH_URL}/reprocess-errors`)
      .flush({ messages: 0 }, { status: 202, statusText: 'Accepted' });
    await settle();
    backend.expectOne(BATCH_URL).flush(batch('CompletedWithErrors', { created: 9, error: 1 }));
    await settle();
    linesRequest().flush(page([line('L1', 1, 'Error', 'personal_data.unavailable')]));
    await settle();

    expect(text()).toContain('Nenhuma linha pôde ser reprocessada');
  });

  it('vazio: filtro sem linhas explica o resultado', async () => {
    await load(batch('Completed', { created: 10 }), page([]));

    expect(text()).toContain('Nenhuma linha neste filtro.');
  });

  it('filtrar por "Com erro" pede a primeira página com o estado', async () => {
    await load(batch('Completed', { created: 10 }), page([line('L1', 1, 'Created')]));

    const select = element.querySelector<HTMLSelectElement>('#line-filter')!;
    select.value = 'Error';
    select.dispatchEvent(new Event('change'));
    const request = linesRequest();
    expect(request.request.params.get('status')).toBe('Error');
    expect(request.request.params.get('page')).toBe('1');
    request.flush(page([]));
    await settle();
  });

  it('polling a cada 3 s enquanto processa; conclui, avisa e para de consultar', async () => {
    vi.useFakeTimers();
    await fixture.whenStable();
    backend.expectOne(BATCH_URL).flush(batch('Processing', { pending: 8, created: 2 }));
    await vi.advanceTimersByTimeAsync(0);
    await fixture.whenStable();
    linesRequest().flush(page([line('L1', 1, 'Pending')]));
    await vi.advanceTimersByTimeAsync(0);
    await fixture.whenStable();
    expect(text()).toContain('2 de 10 linhas processadas');

    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS);
    backend.expectOne(BATCH_URL).flush(batch('Processing', { pending: 4, created: 6 }));
    await vi.advanceTimersByTimeAsync(0);
    linesRequest().flush(page([line('L1', 1, 'Created')]));
    await vi.advanceTimersByTimeAsync(0);
    await fixture.whenStable();
    expect(text()).toContain('6 de 10 linhas processadas');

    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS);
    backend.expectOne(BATCH_URL).flush(batch('Completed', { created: 10 }));
    await vi.advanceTimersByTimeAsync(0);
    linesRequest().flush(page([line('L1', 1, 'Created')]));
    await vi.advanceTimersByTimeAsync(0);
    await fixture.whenStable();
    expect(text()).toContain('Importação concluída. Todas as linhas foram processadas.');

    // Concluído: nenhuma nova consulta (o backend.verify do afterEach falha se houver).
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 3);
  });

  it('nextPollDelay dobra a cada falha até 30 s e volta a 3 s no sucesso', () => {
    let delay = POLL_INTERVAL_MS;
    const seen: number[] = [];
    for (let i = 0; i < 6; i += 1) {
      delay = nextPollDelay(delay, false);
      seen.push(delay);
    }
    expect(seen).toEqual([
      6000,
      12000,
      24000,
      POLL_MAX_INTERVAL_MS,
      POLL_MAX_INTERVAL_MS,
      POLL_MAX_INTERVAL_MS,
    ]);
    expect(nextPollDelay(POLL_MAX_INTERVAL_MS, true)).toBe(POLL_INTERVAL_MS);
  });

  it('polling com falha recua (3 s, 6 s) e volta a 3 s quando a consulta funciona', async () => {
    vi.useFakeTimers();
    await fixture.whenStable();
    backend.expectOne(BATCH_URL).flush(batch('Processing', { pending: 5, created: 5 }));
    await vi.advanceTimersByTimeAsync(0);
    await fixture.whenStable();
    linesRequest().flush(page([]));
    await vi.advanceTimersByTimeAsync(0);

    // 1ª tentativa em 3 s: falha.
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS);
    backend.expectOne(BATCH_URL).flush(null, { status: 500, statusText: 'x' });
    await vi.advanceTimersByTimeAsync(0);
    expect(element.querySelector('[role="alert"]')).not.toBeNull();

    // Próxima só em 6 s: nada em 3 s, uma consulta em 6 s.
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS);
    backend.expectNone(BATCH_URL);
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS);
    backend.expectOne(BATCH_URL).flush(batch('Processing', { pending: 4, created: 6 }));
    await vi.advanceTimersByTimeAsync(0);
    linesRequest().flush(page([]));
    await vi.advanceTimersByTimeAsync(0);
    await fixture.whenStable();
    expect(element.querySelector('[role="alert"]')).toBeNull();

    // Sucesso: de volta a 3 s.
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS);
    backend.expectOne(BATCH_URL).flush(batch('Completed', { created: 10 }));
    await vi.advanceTimersByTimeAsync(0);
    linesRequest().flush(page([]));
    await vi.advanceTimersByTimeAsync(0);
  });

  it('mostra, sem alarme, o resultado da marcação no Legado nas linhas criadas', async () => {
    const withMark = (id: string, n: number, mark: string) => ({
      ...line(id, n, 'Created'),
      legacyMarkStatus: mark,
    });
    await load(
      batch('Completed', { created: 4 }),
      page([
        withMark('A', 1, 'Marked'),
        withMark('B', 2, 'Ambiguous'),
        withMark('C', 3, 'Failed'),
        withMark('D', 4, 'NoCandidate'),
      ]),
    );

    const rows = [...element.querySelectorAll('tbody tr')].map((r) => r.textContent ?? '');
    expect(rows[0]).not.toContain('Legado');
    expect(rows[1]).toContain('Login do Legado não marcado: há mais de um com este e-mail.');
    expect(rows[2]).toContain('Login do Legado não marcado agora. O acesso foi criado.');
    expect(rows[3]).toContain('Sem login correspondente no Legado.');
  });

  it('sair da tela desfaz o intervalo: nada é consultado depois de destruir', async () => {
    vi.useFakeTimers();
    await fixture.whenStable();
    backend.expectOne(BATCH_URL).flush(batch('Processing', { pending: 5, created: 5 }));
    await vi.advanceTimersByTimeAsync(0);
    await fixture.whenStable();
    linesRequest().flush(page([]));
    await vi.advanceTimersByTimeAsync(0);

    fixture.destroy();
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 3);

    expect(vi.getTimerCount()).toBe(0);
  });

  it('lote parado (concluído) nunca arma polling', async () => {
    vi.useFakeTimers();
    await fixture.whenStable();
    backend.expectOne(BATCH_URL).flush(batch('Completed', { created: 10 }));
    await vi.advanceTimersByTimeAsync(0);
    await fixture.whenStable();
    linesRequest().flush(page([]));
    await vi.advanceTimersByTimeAsync(0);

    expect(vi.getTimerCount()).toBe(0);
  });

  it('reprocessar erros: confirma pela API, avisa e atualiza o lote', async () => {
    await load(
      batch('CompletedWithErrors', { created: 8, error: 2 }),
      page([line('L2', 2, 'Error', 'x')], 2),
    );

    button('Reprocessar linhas com erro').click();
    const request = backend.expectOne(`${BATCH_URL}/reprocess-errors`);
    expect(request.request.method).toBe('POST');
    request.flush({ messages: 2 }, { status: 202, statusText: 'Accepted' });
    await settle();
    backend.expectOne(BATCH_URL).flush(batch('Processing', { pending: 2, created: 8 }));
    await settle();
    linesRequest().flush(page([line('L2', 2, 'Pending')]));
    await settle();

    expect(text()).toContain('Linhas com erro reenviadas para a fila de processamento: 2.');
    expect(text()).toContain('Processando');
  });

  it('reprocessar uma linha usa a rota da linha e o botão tem nome acessível', async () => {
    await load(
      batch('CompletedWithErrors', { created: 9, error: 1 }),
      page([line('L7', 7, 'Error', 'x')]),
    );

    const row = element.querySelector('tbody button')!;
    expect(row.getAttribute('aria-label')).toBe('Reprocessar a linha 7');
    [...element.querySelectorAll<HTMLButtonElement>('tbody button')][0].click();
    backend
      .expectOne(`${BATCH_URL}/lines/L7/reprocess`)
      .flush({ messages: 1 }, { status: 202, statusText: 'Accepted' });
    await settle();
    backend.expectOne(BATCH_URL).flush(batch('Processing', { pending: 1, created: 9 }));
    await settle();
    linesRequest().flush(page([line('L7', 7, 'Pending')]));
    await settle();

    expect(text()).toContain('A linha 7 voltou para a fila de processamento.');
  });

  it('403 import.not_owner mostra mensagem de posse, sem step-up', async () => {
    await load(
      batch('CompletedWithErrors', { created: 9, error: 1 }),
      page([line('L7', 7, 'Error', 'x')]),
    );

    button('Reprocessar linhas com erro').click();
    backend
      .expectOne(`${BATCH_URL}/reprocess-errors`)
      .flush({ code: 'import.not_owner' }, { status: 403, statusText: 'Forbidden' });
    await settle();

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Só quem criou o lote');
    expect(element.querySelector('[role="alert"] button')).toBeNull();
    expect(logger.warning).toHaveBeenCalledWith('user_import.reprocess_failed', {
      source: 'user-imports',
      status: 403,
    });
  });

  it('403 sem código (OTP vencido) aciona o step-up voltando ao lote', async () => {
    await load(
      batch('CompletedWithErrors', { created: 9, error: 1 }),
      page([line('L7', 7, 'Error', 'x')]),
    );

    button('Reprocessar linhas com erro').click();
    backend
      .expectOne(`${BATCH_URL}/reprocess-errors`)
      .flush(null, { status: 403, statusText: 'Forbidden' });
    await settle();
    button('Confirmar com código').click();

    expect(auth.requestOtpStepUp).toHaveBeenCalledWith(`/imports/${ID}`);
  });

  it('baixar relatório CSV salva o arquivo recebido', async () => {
    const createUrl = vi.fn(() => 'blob:relatorio');
    const revokeUrl = vi.fn();
    Object.assign(URL, { createObjectURL: createUrl, revokeObjectURL: revokeUrl });
    let downloaded = '';
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      downloaded = this.download;
    });
    await load(batch('Completed', { created: 10 }), page([]));

    button('Baixar relatório CSV').click();
    backend.expectOne(`${BATCH_URL}/report`).flush(new Blob(['linha,estado']));
    await settle();

    expect(createUrl).toHaveBeenCalledWith(expect.any(Blob));
    expect(downloaded).toBe(`relatorio-lote-${ID}.csv`);
    expect(revokeUrl).toHaveBeenCalledWith('blob:relatorio');
    click.mockRestore();
  });

  it('aguardando confirmação: explica e permite cancelar; sem tabela de linhas', async () => {
    await load(batch('AwaitingConfirmation'));

    expect(text()).toContain('Este lote ainda não foi confirmado');
    expect(element.querySelector('table')).toBeNull();
    button('Cancelar lote').click();
    backend.expectOne(`${BATCH_URL}/cancel`).flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    backend.expectOne(BATCH_URL).flush(batch('Cancelled'));
    await settle();

    expect(text()).toContain('Lote cancelado. Nenhuma pessoa foi criada.');
    expect(text()).toContain('Enviar a lista de novo');
  });

  it('lote inexistente (404) mostra mensagem clara e "Tentar de novo"', async () => {
    await fixture.whenStable();
    backend
      .expectOne(BATCH_URL)
      .flush({ code: 'import.not_found' }, { status: 404, statusText: 'Not Found' });
    await settle();

    expect(text()).toContain('Lote não encontrado.');
    expect(button('Tentar de novo')).toBeDefined();
  });
});
