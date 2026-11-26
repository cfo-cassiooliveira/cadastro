import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { UserImportService } from './user-import.service';

const BASE = '/api/v1/user-imports';
const ID = '0b0e3a52-3f0a-4c0e-9d57-0f6c2a6a1111';

describe('UserImportService', () => {
  let service: UserImportService;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(UserImportService);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('upload envia multipart com o campo "file" e devolve o resumo', async () => {
    const file = new File(['nome,email,cpf,telefone\n'], 'lista.csv', { type: 'text/csv' });
    const pending = service.upload(file);

    const request = backend.expectOne(BASE);
    expect(request.request.method).toBe('POST');
    expect((request.request.body as FormData).get('file')).toBeInstanceOf(File);
    request.flush({
      batchId: ID,
      validLines: 2,
      invalidLines: 0,
      duplicateLines: 0,
      summaryToken: 't',
      issues: [],
      issuesTruncated: false,
    });

    const result = await pending;
    expect(result.ok && result.value.batchId).toBe(ID);
  });

  it('409 de arquivo repetido traz o código e o lote existente', async () => {
    const pending = service.upload(new File(['x'], 'a.csv'));
    backend
      .expectOne(BASE)
      .flush(
        { code: 'import.duplicate_file', batchId: ID, batch: {} },
        { status: 409, statusText: 'Conflict' },
      );

    const result = await pending;
    expect(result).toEqual({
      ok: false,
      failure: { status: 409, code: 'import.duplicate_file', batchId: ID, issues: [] },
    });
  });

  it('422 sem linhas válidas traz só os problemas bem formados', async () => {
    const pending = service.upload(new File(['x'], 'a.csv'));
    backend.expectOne(BASE).flush(
      {
        code: 'import.no_valid_lines',
        issues: [{ line: 2, code: 'row.cpf_invalid' }, { line: 'x' }, 'lixo'],
      },
      { status: 422, statusText: 'Unprocessable' },
    );

    const result = await pending;
    expect(!result.ok && result.failure.issues).toEqual([{ line: 2, code: 'row.cpf_invalid' }]);
  });

  it('falha de rede vira status 0, sem código', async () => {
    const pending = service.get(ID);
    backend.expectOne(`${BASE}/${ID}`).error(new ProgressEvent('error'));

    expect(await pending).toEqual({
      ok: false,
      failure: { status: 0, code: null, batchId: null, issues: [] },
    });
  });

  it('confirm manda o token do resumo no corpo', async () => {
    const pending = service.confirm(ID, 'token-123');
    const request = backend.expectOne(`${BASE}/${ID}/confirm`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ summaryToken: 'token-123' });
    request.flush({ messages: 2 }, { status: 202, statusText: 'Accepted' });

    expect(await pending).toEqual({ ok: true, value: { messages: 2 } });
  });

  it('cancel, reprocessamentos e listagens usam as rotas e parâmetros da API', async () => {
    const cancel = service.cancel(ID);
    backend
      .expectOne(`${BASE}/${ID}/cancel`)
      .flush(null, { status: 204, statusText: 'No Content' });
    await cancel;

    const all = service.reprocessErrors(ID);
    backend.expectOne(`${BASE}/${ID}/reprocess-errors`).flush({ messages: 1 });
    await all;

    const one = service.reprocessLine(ID, 'L1');
    backend.expectOne(`${BASE}/${ID}/lines/L1/reprocess`).flush({ messages: 1 });
    await one;

    const list = service.list(2);
    const listRequest = backend.expectOne((r) => r.url === BASE);
    expect(listRequest.request.params.get('page')).toBe('2');
    expect(listRequest.request.params.get('pageSize')).toBe('20');
    listRequest.flush({ items: [], page: 2, pageSize: 20, totalCount: 0 });
    await list;

    const lines = service.lines(ID, 3, 'Error');
    const linesRequest = backend.expectOne((r) => r.url === `${BASE}/${ID}/lines`);
    expect(linesRequest.request.params.get('status')).toBe('Error');
    expect(linesRequest.request.params.get('page')).toBe('3');
    linesRequest.flush({ items: [], page: 3, pageSize: 20, totalCount: 0 });
    await lines;
  });

  it('report pede o CSV como blob', async () => {
    const pending = service.report(ID);
    const request = backend.expectOne(`${BASE}/${ID}/report`);
    expect(request.request.responseType).toBe('blob');
    request.flush(new Blob(['a']));

    expect((await pending).ok).toBe(true);
  });
});
