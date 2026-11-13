import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { createLoggerSpy, LoggerSpy, provideLoggerSpy } from '../core/app-logger.testing';
import { BrowserNavigation } from '../core/browser-navigation';
import { AuthService } from './auth-service';

describe('AuthService', () => {
  let service: AuthService;
  let http: HttpTestingController;
  let logger: LoggerSpy;
  const navigation = { assign: vi.fn(), currentPath: vi.fn() };

  beforeEach(() => {
    navigation.assign.mockClear();
    logger = createLoggerSpy();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideLoggerSpy(logger),
        { provide: BrowserNavigation, useValue: navigation },
      ],
    });
    service = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('carrega a sessão do BFF com login e roles', async () => {
    const loading = service.load();
    http.expectOne('/bff/user').flush({
      isAuthenticated: true,
      userName: 'ana.souza',
      roles: ['user', 'admin'],
      antiforgeryToken: 'csrf-1',
    });
    await loading;

    expect(service.isAuthenticated()).toBe(true);
    expect(service.isAdmin()).toBe(true);
    expect(service.user().userName).toBe('ana.souza');
  });

  it('trata falha ao ler a sessão como visitante e registra erro', async () => {
    const loading = service.load();
    http.expectOne('/bff/user').flush(null, { status: 502, statusText: 'Bad Gateway' });
    await loading;

    expect(service.isAuthenticated()).toBe(false);
    expect(logger.error).toHaveBeenCalledWith(
      'auth.session_load_failed',
      { source: 'auth' },
      expect.anything(),
    );
  });

  it('entra pelo BFF com o destino codificado', () => {
    service.login('/profile?tab=1');

    expect(navigation.assign).toHaveBeenCalledWith('/bff/login?returnUrl=%2Fprofile%3Ftab%3D1');
  });

  it('sai no BFF e segue para o logout do Keycloak', async () => {
    const leaving = service.logout();
    const request = http.expectOne('/bff/logout');
    expect(request.request.method).toBe('POST');
    request.flush({
      logoutUrl:
        'http://localhost:8180/realms/workspace/protocol/openid-connect/logout?state=signed-out',
    });
    await leaving;

    expect(service.isAuthenticated()).toBe(false);
    expect(navigation.assign).toHaveBeenCalledWith(
      'http://localhost:8180/realms/workspace/protocol/openid-connect/logout?state=signed-out',
    );
    expect(logger.info).toHaveBeenCalledWith('auth.signed_out', { source: 'auth' });
  });

  it('quando o logout falha, registra erro e relê a sessão', async () => {
    const leaving = service.logout();
    http.expectOne('/bff/logout').flush(null, { status: 500, statusText: 'Server Error' });
    await vi.waitFor(() =>
      http
        .expectOne('/bff/user')
        .flush({ isAuthenticated: false, userName: null, roles: [], antiforgeryToken: null }),
    );
    await leaving;

    expect(logger.error).toHaveBeenCalledWith(
      'auth.logout_failed',
      { source: 'auth' },
      expect.anything(),
    );
    expect(navigation.assign).not.toHaveBeenCalled();
  });
});
