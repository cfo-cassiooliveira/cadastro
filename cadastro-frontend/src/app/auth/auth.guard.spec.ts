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
import { authGuard, roleGuard, visitorGuard } from './auth.guard';

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

describe('roleGuard', () => {
  const isAuthenticated = signal(false);
  const hasRole = vi.fn();
  const login = vi.fn();
  const route = {} as ActivatedRouteSnapshot;
  const state = { url: '/log-search' } as RouterStateSnapshot;

  beforeEach(() => {
    isAuthenticated.set(false);
    hasRole.mockReset();
    login.mockClear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { isAuthenticated, hasRole, login } },
      ],
    });
  });

  it('deixa passar quem tem a role', () => {
    isAuthenticated.set(true);
    hasRole.mockReturnValue(true);

    expect(TestBed.runInInjectionContext(() => roleGuard('log-investigator')(route, state))).toBe(
      true,
    );
    expect(hasRole).toHaveBeenCalledWith('log-investigator');
  });

  it('manda quem entrou sem a role para o acesso negado', () => {
    isAuthenticated.set(true);
    hasRole.mockReturnValue(false);

    const result = TestBed.runInInjectionContext(() =>
      roleGuard('log-investigator')(route, state),
    ) as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/forbidden');
  });

  it('leva o visitante ao login e volta para a mesma página', () => {
    expect(TestBed.runInInjectionContext(() => roleGuard('log-investigator')(route, state))).toBe(
      false,
    );
    expect(login).toHaveBeenCalledWith('/log-search');
  });
});
