import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  provideRouter,
  Router,
  RouterStateSnapshot,
  UrlTree,
} from '@angular/router';
import { AuthService } from './auth-service';
import { authGuard, visitorGuard } from './auth.guard';

describe('guards de sessão', () => {
  const isAuthenticated = signal(false);
  const login = vi.fn();

  beforeEach(() => {
    isAuthenticated.set(false);
    login.mockClear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { isAuthenticated, login } },
      ],
    });
  });

  const route = {} as ActivatedRouteSnapshot;
  const state = { url: '/profile' } as RouterStateSnapshot;

  it('authGuard deixa passar quem entrou', () => {
    isAuthenticated.set(true);

    expect(TestBed.runInInjectionContext(() => authGuard(route, state))).toBe(true);
    expect(login).not.toHaveBeenCalled();
  });

  it('authGuard leva o visitante ao login e volta para a mesma página', () => {
    expect(TestBed.runInInjectionContext(() => authGuard(route, state))).toBe(false);
    expect(login).toHaveBeenCalledWith('/profile');
  });

  it('visitorGuard manda quem já entrou para o perfil', () => {
    isAuthenticated.set(true);

    const result = TestBed.runInInjectionContext(() => visitorGuard(route, state)) as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/profile');
  });

  it('visitorGuard deixa o visitante ver o Início', () => {
    expect(TestBed.runInInjectionContext(() => visitorGuard(route, state))).toBe(true);
  });
});
