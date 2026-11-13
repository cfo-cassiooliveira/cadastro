import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from './auth-service';

/** Header que o BFF confere junto com o cookie HttpOnly de antiforgery. */
export const ANTIFORGERY_HEADER = 'X-XSRF-TOKEN';

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

/** Envia o token anti-CSRF (guardado em memória) nas chamadas que mudam estado, só para a própria origem. */
export const antiforgeryInterceptor: HttpInterceptorFn = (request, next) => {
  const token = inject(AuthService).antiforgeryToken();
  const sameOrigin = request.url.startsWith('/') && !request.url.startsWith('//');
  if (!token || !sameOrigin || SAFE_METHODS.has(request.method)) {
    return next(request);
  }
  return next(request.clone({ setHeaders: { [ANTIFORGERY_HEADER]: token } }));
};
