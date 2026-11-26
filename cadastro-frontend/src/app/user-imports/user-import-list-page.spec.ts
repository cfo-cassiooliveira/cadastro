import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth-service';
import { createLoggerSpy, LoggerSpy, provideLoggerSpy } from '../core/app-logger.testing';
import { UserImportListPage } from './user-import-list-page';

const BASE = '/api/v1/user-imports';

function batch(id: string, status: string, error = 0) {
  return {
    id,
    status,
    validLines: 10,
    invalidLines: 1,
    duplicateLines: 2,
    createdAtUtc: '2026-11-25T15:30:00Z',
    confirmedAtUtc: null,
    finishedAtUtc: null,
    progress: { pending: 2, created: 6, alreadyExists: 0, error, cancelled: 0 },
  };
}

describe('UserImportListPage', () => {
  let fixture: ComponentFixture<UserImportListPage>;
  let element: HTMLElement;
  let backend: HttpTestingController;
  let logger: LoggerSpy;
  const auth = { requestOtpStepUp: vi.fn() };

  beforeEach(() => {
    logger = createLoggerSpy();
    auth.requestOtpStepUp.mockReset();
    TestBed.configureTestingModule({
      imports: [UserImportListPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideLoggerSpy(logger),
        { provide: AuthService, useValue: auth },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(UserImportListPage);
    element = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => backend.verify());

  const text = () => element.textContent ?? '';

  async function reply(body: object | null, status = 200): Promise<void> {
    backend.expectOne((r) => r.url === BASE).flush(body, { status, statusText: 'x' });
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
  }

  it('carregando: mostra o esqueleto com aviso acessível', async () => {
    await fixture.whenStable();

    expect(element.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(text()).toContain('Carregando os lotes…');
    await reply({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  });

  it('vazio: explica e oferece enviar a primeira lista', async () => {
    await fixture.whenStable();
    await reply({ items: [], page: 1, pageSize: 20, totalCount: 0 });

    expect(text()).toContain('Nenhum lote ainda');
    expect(element.querySelector('.state a')?.getAttribute('href')).toBe('/imports/new');
  });

  it('sucesso: lista os lotes com estado, contagens e progresso', async () => {
    await fixture.whenStable();
    await reply({
      items: [
        batch('a1', 'Processing'),
        batch('b2', 'CompletedWithErrors', 1),
        batch('c3', 'AwaitingConfirmation'),
      ],
      page: 1,
      pageSize: 20,
      totalCount: 3,
    });

    const items = element.querySelectorAll('li.batch');
    expect(items).toHaveLength(3);
    expect(items[0].textContent).toContain('Processando');
    expect(items[0].textContent).toContain('Válidas: 10 · Inválidas: 1 · Duplicadas: 2');
    expect(items[0].querySelector('app-import-progress')).not.toBeNull();
    expect(items[1].textContent).toContain('Concluído com erros');
    expect(items[2].querySelector('app-import-progress')).toBeNull();
    expect(items[0].querySelector('a')?.getAttribute('href')).toBe('/imports/a1');
    expect(text()).not.toMatch(/undefined|null|NaN/);
  });

  it('paginação: "Próxima" pede a página 2', async () => {
    await fixture.whenStable();
    await reply({ items: [batch('a1', 'Completed')], page: 1, pageSize: 20, totalCount: 45 });

    expect(text()).toContain('Página 1 de 3');
    [...element.querySelectorAll('button')]
      .find((b) => b.textContent?.includes('Próxima'))!
      .click();
    const request = backend.expectOne((r) => r.url === BASE);
    expect(request.request.params.get('page')).toBe('2');
    request.flush({ items: [batch('z9', 'Completed')], page: 2, pageSize: 20, totalCount: 45 });
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();

    expect(text()).toContain('Página 2 de 3');
  });

  it('erro: mensagem, log e "Tentar de novo" recarrega', async () => {
    await fixture.whenStable();
    await reply(null, 500);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain(
      'Não foi possível concluir agora.',
    );
    expect(logger.error).toHaveBeenCalledWith('user_import.list_failed', {
      source: 'user-imports',
      status: 500,
    });

    [...element.querySelectorAll('button')]
      .find((b) => b.textContent?.includes('Tentar de novo'))!
      .click();
    await reply({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    expect(text()).toContain('Nenhum lote ainda');
  });

  it('403 sem código aciona o step-up pela tela de lotes', async () => {
    await fixture.whenStable();
    await reply(null, 403);

    [...element.querySelectorAll('button')]
      .find((b) => b.textContent?.includes('Confirmar com código'))!
      .click();
    expect(auth.requestOtpStepUp).toHaveBeenCalledWith('/imports');
  });
});
