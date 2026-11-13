import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AppLogger } from './app-logger';
import { CORRELATION_ID_HEADER } from './correlation-id.interceptor';

describe('AppLogger', () => {
  let logger: AppLogger;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    logger = TestBed.inject(AppLogger);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('info envia nível information com evento e origem', () => {
    logger.info('users.registered', { source: 'register-page' });

    const request = http.expectOne('/api/v1/client-logs');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      level: 'information',
      event: 'users.registered',
      source: 'register-page',
      status: undefined,
      message: '',
    });
    expect(request.request.headers.get(CORRELATION_ID_HEADER)).toMatch(/^[0-9a-f-]{36}$/);
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('warning envia nível warning com status e mensagem', () => {
    logger.warning('auth.sign_in_rejected', { source: 'login-page', status: 401 }, 'codes');

    expect(http.expectOne('/api/v1/client-logs').request.body).toMatchObject({
      level: 'warning',
      status: 401,
      message: 'codes',
    });
  });

  it('error de HTTP envia só status, sem a URL (pode conter dados do usuário)', () => {
    const error = new HttpErrorResponse({
      status: 503,
      statusText: 'Service Unavailable',
      url: '/api/v1/addresses/postal-codes/01001000',
    });

    logger.error('address.postal_code_lookup_failed', { source: 'register-page' }, error);

    const body = http.expectOne('/api/v1/client-logs').request.body as Record<string, unknown>;
    expect(body).toMatchObject({
      level: 'error',
      status: 503,
      message: 'HTTP 503 Service Unavailable',
    });
    expect(JSON.stringify(body)).not.toContain('01001000');
  });

  it('error de JavaScript envia nome, mensagem e stack', () => {
    logger.error('app.unhandled_error', { source: 'global' }, new TypeError('x is undefined'));

    const body = http.expectOne('/api/v1/client-logs').request.body as Record<string, unknown>;
    expect(body['message']).toBe('TypeError: x is undefined');
    expect(body['stack']).toContain('TypeError');
  });

  it('falha ao enviar o log não lança erro', async () => {
    logger.error('app.unhandled_error', { source: 'global' }, new Error('boom'));

    http.expectOne('/api/v1/client-logs').flush(null, { status: 500, statusText: 'Error' });
    await Promise.resolve();

    expect(true).toBe(true);
  });
});
