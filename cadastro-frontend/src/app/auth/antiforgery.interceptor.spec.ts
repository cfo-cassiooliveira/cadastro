import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthService } from './auth-service';
import { ANTIFORGERY_HEADER, antiforgeryInterceptor } from './antiforgery.interceptor';

describe('antiforgeryInterceptor', () => {
  const antiforgeryToken = signal<string | null>('csrf-123');
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    antiforgeryToken.set('csrf-123');
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([antiforgeryInterceptor])),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: { antiforgeryToken } },
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('envia o token em POST para a própria origem', () => {
    http.post('/bff/logout', null).subscribe();

    expect(backend.expectOne('/bff/logout').request.headers.get(ANTIFORGERY_HEADER)).toBe(
      'csrf-123',
    );
  });

  it('não envia em GET', () => {
    http.get('/api/v1/users/me').subscribe();

    expect(backend.expectOne('/api/v1/users/me').request.headers.has(ANTIFORGERY_HEADER)).toBe(
      false,
    );
  });

  it('nunca envia para outra origem', () => {
    http.post('https://outro.example.com/x', null).subscribe();
    http.post('//outro.example.com/y', null).subscribe();

    expect(
      backend.expectOne('https://outro.example.com/x').request.headers.has(ANTIFORGERY_HEADER),
    ).toBe(false);
    expect(backend.expectOne('//outro.example.com/y').request.headers.has(ANTIFORGERY_HEADER)).toBe(
      false,
    );
  });

  it('sem token (sessão ainda não lida) segue sem o header', () => {
    antiforgeryToken.set(null);
    http.post('/bff/logout', null).subscribe();

    expect(backend.expectOne('/bff/logout').request.headers.has(ANTIFORGERY_HEADER)).toBe(false);
  });
});
