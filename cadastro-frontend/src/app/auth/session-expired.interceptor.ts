import { HttpErrorResponse, HttpInterceptorFn, HttpStatusCode } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AppLogger } from '../core/app-logger';
import { BrowserNavigation } from '../core/browser-navigation';
import { AuthService } from './auth-service';
import { SESSION_EXPIRED_PARAM, SESSION_EXPIRED_VALUE } from './auth.model';

/**
 * 401 da API = sessão terminou no servidor (inatividade, logout em outro lugar, conta desativada). Leva ao login e,
 * na volta, a tela avisa "Sua sessão expirou. Entre novamente.".
 */
export const sessionExpiredInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const navigation = inject(BrowserNavigation);
  const logger = inject(AppLogger);
  return next(request).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === HttpStatusCode.Unauthorized &&
        request.url.startsWith('/api/')
      ) {
        logger.warning('auth.session_expired', { source: 'auth', status: error.status });
        auth.login(withSessionExpiredMark(navigation.currentPath()));
      }
      return throwError(() => error);
    }),
  );
};

function withSessionExpiredMark(path: string): string {
  const [pathname, query = ''] = path.split('?');
  const params = new URLSearchParams(query);
  params.set(SESSION_EXPIRED_PARAM, SESSION_EXPIRED_VALUE);
  return `${pathname}?${params.toString()}`;
}
