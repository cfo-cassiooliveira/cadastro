import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { createLoggerSpy, LoggerSpy, provideLoggerSpy } from '../core/app-logger.testing';
import { BrowserNavigation } from '../core/browser-navigation';
import { AuthService } from './auth-service';
import { sessionExpiredInterceptor } from './session-expired.interceptor';

describe('sessionExpiredInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let logger: LoggerSpy;
  const login = vi.fn();

  beforeEach(() => {
    login.mockClear();
    logger = createLoggerSpy();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([sessionExpiredInterceptor])),
        provideHttpClientTesting(),
        provideLoggerSpy(logger),
        { provide: AuthService, useValue: { login } },
        { provide: BrowserNavigation, useValue: { currentPath: () => '/profile?tab=1' } },
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  async function request(url: string, status: number): Promise<void> {
    const call = firstValueFrom(http.get(url)).catch(() => undefined);
    backend.expectOne(url).flush(null, { status, statusText: 'x' });
    await call;
  }

  it('401 da API leva ao login marcando a sessão expirada', async () => {
    await request('/api/v1/users/me', 401);

    expect(login).toHaveBeenCalledWith('/profile?tab=1&session=expired');
    expect(logger.warning).toHaveBeenCalledWith('auth.session_expired', {
      source: 'auth',
      status: 401,
    });
  });

  it('401 fora da API (BFF) não redireciona', async () => {
    await request('/bff/logout', 401);

    expect(login).not.toHaveBeenCalled();
  });

  it('outros erros seguem para a tela tratar', async () => {
    await request('/api/v1/users/me', 503);

    expect(login).not.toHaveBeenCalled();
  });
});
