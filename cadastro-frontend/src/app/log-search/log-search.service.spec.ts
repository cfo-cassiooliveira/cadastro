import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { LogSearchService } from './log-search.service';

describe('LogSearchService.reportAccessDenied', () => {
  let backend: HttpTestingController;
  let service: LogSearchService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    backend = TestBed.inject(HttpTestingController);
    service = TestBed.inject(LogSearchService);
  });

  it('faz GET sem corpo em /access', async () => {
    const done = service.reportAccessDenied();
    const request = backend.expectOne('/api/v1/log-search/access');
    expect(request.request.method).toBe('GET');
    request.flush(null, { status: 403, statusText: 'Forbidden' });

    await expect(done).resolves.toBeUndefined();
  });

  it('ignora falha de rede sem lançar', async () => {
    const done = service.reportAccessDenied();
    backend.expectOne('/api/v1/log-search/access').error(new ProgressEvent('error'));

    await expect(done).resolves.toBeUndefined();
  });
});
