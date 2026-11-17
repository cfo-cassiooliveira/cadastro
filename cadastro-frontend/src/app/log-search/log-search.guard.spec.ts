import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, provideRouter, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { AuthService } from '../auth/auth-service';
import { logSearchGuard } from './log-search.guard';
import { LogSearchService } from './log-search.service';

describe('logSearchGuard', () => {
  const isAuthenticated = signal(false);
  const hasRole = vi.fn();
  const login = vi.fn();
  const reportAccessDenied = vi.fn();
  const route = {} as ActivatedRouteSnapshot;
  const state = { url: '/log-search' } as RouterStateSnapshot;

  beforeEach(() => {
    isAuthenticated.set(true);
    hasRole.mockReset();
    login.mockClear();
    reportAccessDenied.mockReset().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { isAuthenticated, hasRole, login } },
        { provide: LogSearchService, useValue: { reportAccessDenied } },
      ],
    });
  });

  const run = () => TestBed.runInInjectionContext(() => logSearchGuard('log-investigator')(route, state));

  it('sem a role registra o acesso negado e vai a /forbidden', () => {
    hasRole.mockReturnValue(false);

    const result = run() as UrlTree;

    expect(reportAccessDenied).toHaveBeenCalledTimes(1);
    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/forbidden');
  });

  it('vai a /forbidden mesmo se o registro falhar', async () => {
    hasRole.mockReturnValue(false);
    reportAccessDenied.mockRejectedValue(new Error('x'));

    const result = run() as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/forbidden');
  });

  it('com a role deixa passar e não chama a API', () => {
    hasRole.mockReturnValue(true);

    expect(run()).toBe(true);
    expect(reportAccessDenied).not.toHaveBeenCalled();
  });

  it('visitante vai ao login e não chama a API', () => {
    isAuthenticated.set(false);

    expect(run()).toBe(false);
    expect(login).toHaveBeenCalledWith('/log-search');
    expect(reportAccessDenied).not.toHaveBeenCalled();
  });
});
