import {
  ApplicationConfig,
  ErrorHandler,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideHttpClient, withInterceptors, withNoXsrfProtection } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { antiforgeryInterceptor } from './auth/antiforgery.interceptor';
import { AuthService } from './auth/auth-service';
import { sessionExpiredInterceptor } from './auth/session-expired.interceptor';
import { correlationIdInterceptor } from './core/correlation-id.interceptor';
import { GlobalErrorHandler } from './core/global-error-handler';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Erros não tratados vão para o log da API (Graylog) em vez de só ao console.
    { provide: ErrorHandler, useClass: GlobalErrorHandler },
    provideRouter(routes, withComponentInputBinding()),
    // Token anti-CSRF do BFF: vem em /bff/user, fica em memória e vai no header pelo antiforgeryInterceptor.
    // O XSRF padrão do Angular (cookie legível) fica desligado: o cookie do BFF é HttpOnly.
    provideHttpClient(
      withInterceptors([
        correlationIdInterceptor,
        antiforgeryInterceptor,
        sessionExpiredInterceptor,
      ]),
      withNoXsrfProtection(),
    ),
    // A sessão é lida antes da primeira rota: guards e cabeçalho já nascem com o estado certo.
    provideAppInitializer(() => inject(AuthService).load()),
  ],
};
