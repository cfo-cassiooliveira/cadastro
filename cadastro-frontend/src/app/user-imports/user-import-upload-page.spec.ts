import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthService } from '../auth/auth-service';
import { createLoggerSpy, LoggerSpy, provideLoggerSpy } from '../core/app-logger.testing';
import { UserImportUploadPage, validateFile } from './user-import-upload-page';

const BASE = '/api/v1/user-imports';
const ID = '0b0e3a52-3f0a-4c0e-9d57-0f6c2a6a1111';
const TOKEN = 'token-do-resumo-em-memoria';
const SUMMARY = {
  batchId: ID,
  validLines: 3,
  invalidLines: 1,
  duplicateLines: 1,
  summaryToken: TOKEN,
  issues: [
    { line: 4, code: 'row.cpf_invalid' },
    { line: 5, code: 'row.duplicate_email' },
  ],
  issuesTruncated: false,
};

describe('validateFile', () => {
  it('exige arquivo, extensão .csv, conteúdo e no máximo 5 MB', () => {
    expect(validateFile(null)).toBe('Selecione um arquivo CSV.');
    expect(validateFile(new File(['x'], 'lista.txt'))).toBe(
      'O arquivo precisa ser um CSV (extensão .csv).',
    );
    expect(validateFile(new File([], 'lista.csv'))).toBe('O arquivo está vazio.');
    const big = new File(['x'], 'lista.csv');
    Object.defineProperty(big, 'size', { value: 5 * 1024 * 1024 + 1 });
    expect(validateFile(big)).toBe('O arquivo passa de 5 MB. Divida a lista em arquivos menores.');
    expect(validateFile(new File(['x'], 'LISTA.CSV'))).toBeNull();
  });
});

describe('UserImportUploadPage', () => {
  let fixture: ComponentFixture<UserImportUploadPage>;
  let element: HTMLElement;
  let backend: HttpTestingController;
  let logger: LoggerSpy;
  let router: Router;
  const auth = { requestOtpStepUp: vi.fn() };

  beforeEach(async () => {
    logger = createLoggerSpy();
    auth.requestOtpStepUp.mockReset();
    localStorage.clear();
    sessionStorage.clear();
    TestBed.configureTestingModule({
      imports: [UserImportUploadPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideLoggerSpy(logger),
        { provide: AuthService, useValue: auth },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(UserImportUploadPage);
    element = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  afterEach(() => backend.verify());

  const text = () => element.textContent ?? '';
  const button = (label: string) =>
    [...element.querySelectorAll('button')].find((b) => b.textContent?.includes(label))!;

  async function choose(file: File | null): Promise<void> {
    const input = element.querySelector<HTMLInputElement>('#csv-file')!;
    Object.defineProperty(input, 'files', { value: file ? [file] : [], configurable: true });
    input.dispatchEvent(new Event('change'));
    await fixture.whenStable();
  }

  async function submit(): Promise<void> {
    element.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
  }

  async function reply(
    request: ReturnType<HttpTestingController['expectOne']>,
    body: object | null,
    status: number,
  ) {
    request.flush(body, { status, statusText: 'x' });
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
  }

  async function uploadValidFile(): Promise<void> {
    await choose(new File(['nome,email,cpf,telefone\n'], 'lista.csv', { type: 'text/csv' }));
    await submit();
    await reply(backend.expectOne(BASE), SUMMARY, 201);
  }

  it('estado inicial: título, formato esperado, link do CSV de exemplo e rótulo no campo', () => {
    expect(element.querySelector('h1')?.textContent).toBe('Enviar lista de pessoas');
    expect(element.querySelector('label[for="csv-file"]')?.textContent).toBe('Arquivo CSV');
    expect(text()).toContain('nome,email,cpf,telefone');
    const sample = element.querySelector<HTMLAnchorElement>('a[download]')!;
    expect(sample.getAttribute('href')).toBe('exemplos/usuarios-exemplo.csv');
  });

  it('enviar sem arquivo mostra o erro junto do campo e não chama a API', async () => {
    await submit();

    expect(element.querySelector('#file-error')?.textContent).toBe('Selecione um arquivo CSV.');
    expect(element.querySelector('#csv-file')?.getAttribute('aria-invalid')).toBe('true');
  });

  it('extensão errada é recusada no cliente, sem chamar a API', async () => {
    await choose(new File(['x'], 'lista.xlsx'));
    await submit();

    expect(element.querySelector('#file-error')?.textContent).toContain('extensão .csv');
  });

  it('carregando: desabilita o envio e anuncia a verificação', async () => {
    await choose(new File(['x'], 'lista.csv'));
    await submit();

    expect(element.querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(true);
    expect(text()).toContain('Verificando o arquivo…');
    backend.expectOne(BASE).flush(SUMMARY, { status: 201, statusText: 'Created' });
  });

  it('sucesso: mostra válidas, inválidas, duplicadas e os problemas por linha com motivo traduzido', async () => {
    await uploadValidFile();

    expect(element.querySelector('#summary-title')?.textContent).toContain('Resumo do arquivo');
    const counts = [...element.querySelectorAll('.counts__item')].map((i) =>
      i.textContent?.replace(/\s+/g, ' ').trim(),
    );
    expect(counts).toEqual(['Válidas3', 'Inválidas1', 'Duplicadas1']);
    const rows = [...element.querySelectorAll('tbody tr')].map((r) =>
      r.textContent?.replace(/\s+/g, ' ').trim(),
    );
    expect(rows).toEqual(['4CPF inválido.', '5E-mail repetido no arquivo.']);
    expect(text()).not.toContain('row.');
  });

  it('confirmar manda o token guardado em memória, limpa o token e vai ao lote', async () => {
    await uploadValidFile();

    button('Confirmar e criar os acessos').click();
    const request = backend.expectOne(`${BASE}/${ID}/confirm`);
    expect(request.request.body).toEqual({ summaryToken: TOKEN });
    await reply(request, { messages: 3 }, 202);

    expect(router.navigate).toHaveBeenCalledWith(['/imports', ID]);
    expect(logger.info).toHaveBeenCalledWith('user_import.confirmed', { source: 'user-imports' });
  });

  it('o token não vai a storage, ao DOM nem ao logger', async () => {
    await uploadValidFile();

    expect(JSON.stringify({ ...localStorage })).not.toContain(TOKEN);
    expect(JSON.stringify({ ...sessionStorage })).not.toContain(TOKEN);
    expect(element.innerHTML).not.toContain(TOKEN);
    expect(
      JSON.stringify(logger.warning.mock.calls) + JSON.stringify(logger.error.mock.calls),
    ).not.toContain(TOKEN);
  });

  it('cancelar descarta o lote e volta ao formulário com aviso', async () => {
    await uploadValidFile();

    button('Cancelar lote').click();
    await reply(backend.expectOne(`${BASE}/${ID}/cancel`), null, 204);

    expect(element.querySelector('#csv-file')).not.toBeNull();
    expect(text()).toContain('Lote cancelado. Nenhuma pessoa foi criada.');
  });

  it('403 sem código no upload pede confirmação com código e aciona o step-up da tela', async () => {
    await choose(new File(['x'], 'lista.csv'));
    await submit();
    await reply(backend.expectOne(BASE), null, 403);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain(
      'código do autenticador',
    );
    button('Confirmar com código').click();
    expect(auth.requestOtpStepUp).toHaveBeenCalledWith('/imports/new');
  });

  it('409 de arquivo repetido oferece o link do lote existente', async () => {
    await choose(new File(['x'], 'lista.csv'));
    await submit();
    await reply(backend.expectOne(BASE), { code: 'import.duplicate_file', batchId: ID }, 409);

    expect(text()).toContain('Este arquivo já foi enviado.');
    const link = [...element.querySelectorAll('a')].find((a) =>
      a.textContent?.includes('Ver o lote existente'),
    )!;
    expect(link.getAttribute('href')).toBe(`/imports/${ID}`);
    expect(logger.warning).toHaveBeenCalledWith('user_import.upload_failed', {
      source: 'user-imports',
      status: 409,
    });
  });

  it('422 sem linhas válidas mostra a mensagem e a tabela de problemas', async () => {
    await choose(new File(['x'], 'lista.csv'));
    await submit();
    await reply(
      backend.expectOne(BASE),
      { code: 'import.no_valid_lines', issues: [{ line: 2, code: 'row.email_invalid' }] },
      422,
    );

    expect(text()).toContain('Nenhuma linha do arquivo é válida.');
    expect(text()).toContain('E-mail inválido.');
  });

  it('503 do antivírus: mensagem clara e log em nível error, sem dado do arquivo', async () => {
    await choose(new File(['x'], 'lista.csv'));
    await submit();
    await reply(backend.expectOne(BASE), { code: 'scanner.unavailable' }, 503);

    expect(text()).toContain('verificação de segurança do arquivo está indisponível');
    expect(logger.error).toHaveBeenCalledWith('user_import.upload_failed', {
      source: 'user-imports',
      status: 503,
    });
  });

  it('erro de rede mostra mensagem genérica sem vazamento técnico', async () => {
    await choose(new File(['x'], 'lista.csv'));
    await submit();
    backend.expectOne(BASE).error(new ProgressEvent('error'));
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();

    expect(text()).toContain('Não foi possível concluir agora.');
    expect(text()).not.toMatch(/undefined|null|\[object/);
  });

  it('confirmar com token recusado (400) mostra a mensagem e mantém o resumo', async () => {
    await uploadValidFile();

    button('Confirmar e criar os acessos').click();
    await reply(
      backend.expectOne(`${BASE}/${ID}/confirm`),
      { code: 'import.confirmation_token_invalid' },
      400,
    );

    expect(text()).toContain('A confirmação não corresponde a este resumo.');
    expect(element.querySelector('#summary-title')).not.toBeNull();
  });
});
