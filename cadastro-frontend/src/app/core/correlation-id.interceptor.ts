import { HttpInterceptorFn } from '@angular/common/http';

export const CORRELATION_ID_HEADER = 'X-Correlation-Id';

/** Envia um X-Correlation-Id por requisição para rastrear a chamada nos logs da API. */
export const correlationIdInterceptor: HttpInterceptorFn = (request, next) =>
  next(request.clone({ setHeaders: { [CORRELATION_ID_HEADER]: crypto.randomUUID() } }));
