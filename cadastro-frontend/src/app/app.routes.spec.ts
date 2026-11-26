import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { routes } from './app.routes';
import { AuthService } from './auth/auth-service';

describe('rotas de importação em lote', () => {
  const isAuthenticated = signal(true);
  const hasRole = vi.fn();
  const login = vi.fn();

  beforeEach(() => {
    isAuthenticated.set(true);
    hasRole.mockReset();
    login.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        { provide: AuthService, useValue: { isAuthenticated, hasRole, login } },
      ],
    });
  });

  it.each(['/imports', '/imports/new', '/imports/0b0e3a52-3f0a-4c0e-9d57-0f6c2a6a1111'])(
    'sem a role user-importer, %s vai a /forbidden',
    async (url) => {
      hasRole.mockReturnValue(false);

      await TestBed.inject(Router).navigateByUrl(url);

      expect(TestBed.inject(Router).url).toBe('/forbidden');
      expect(hasRole).toHaveBeenCalledWith('user-importer');
    },
  );

  it('visitante é levado ao login e volta para a mesma rota', async () => {
    isAuthenticated.set(false);

    await TestBed.inject(Router).navigateByUrl('/imports/new');

    expect(login).toHaveBeenCalledWith('/imports/new');
  });
});
