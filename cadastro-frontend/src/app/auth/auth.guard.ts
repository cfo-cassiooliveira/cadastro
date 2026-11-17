import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth-service';

/** Início é para visitante: quem já entrou vai direto ao perfil. */
export const visitorGuard: CanActivateFn = () =>
  inject(AuthService).isAuthenticated() ? inject(Router).parseUrl('/profile') : true;

/** Rota só para quem entrou: visitante vai ao login e volta para a mesma página. */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  if (auth.isAuthenticated()) {
    return true;
  }
  auth.login(state.url);
  return false;
};

/**
 * Rota por role: visitante vai ao login (e volta); quem entrou sem a role vai à tela de acesso negado. A API continua
 * sendo a autoridade (403), este guard só evita mostrar uma tela que o usuário não pode usar.
 */
export function roleGuard(role: string): CanActivateFn {
  return (_route, state) => {
    const auth = inject(AuthService);
    if (!auth.isAuthenticated()) {
      auth.login(state.url);
      return false;
    }
    return auth.hasRole(role) ? true : inject(Router).parseUrl('/forbidden');
  };
}
