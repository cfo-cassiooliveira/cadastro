import { inject } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import { roleGuard } from '../auth/auth.guard';
import { LogSearchService } from './log-search.service';

/**
 * Guard de `/log-search`: aplica o `roleGuard` e, quando quem entrou não tem a role, avisa a API (sem corpo nem dado
 * digitado) para que a tentativa fique na trilha de auditoria. A navegação a `/forbidden` não espera nem depende da chamada.
 */
export function logSearchGuard(role: string): CanActivateFn {
  const guard = roleGuard(role);
  return (route, state) => {
    const search = inject(LogSearchService);
    const result = guard(route, state);
    // Só um UrlTree (acesso negado por falta de role) dispara o registro; `true`/`false` (login) não.
    if (typeof result === 'object' && result !== null && 'root' in result) {
      void search.reportAccessDenied();
    }
    return result;
  };
}
