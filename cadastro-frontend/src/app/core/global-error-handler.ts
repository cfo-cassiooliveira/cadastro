import { ErrorHandler, inject, Injectable, isDevMode } from '@angular/core';
import { AppLogger } from './app-logger';

/** Indica se o build é de desenvolvimento (isolado para os testes poderem simular produção). */
export const DEV_MODE = { enabled: (): boolean => isDevMode() };

/**
 * Captura todo erro não tratado da aplicação (inclusive os de window/promise, encaminhados por
 * provideBrowserGlobalErrorListeners) e o registra como error, para rastreio no Graylog.
 */
@Injectable()
export class GlobalErrorHandler implements ErrorHandler {
  private readonly logger = inject(AppLogger);

  handleError(error: unknown): void {
    this.logger.error('app.unhandled_error', { source: 'global' }, error);
    if (DEV_MODE.enabled()) {
      // Só em desenvolvimento: mantém o stack trace navegável no DevTools. Em produção nada vai ao console.
      console.error(error);
    }
  }
}
