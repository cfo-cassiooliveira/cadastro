import { AppLogger } from '../core/app-logger';
import { ApiFailure } from './user-import.model';

export const LOGGER_SOURCE = 'user-imports';

/**
 * Registra uma falha da API sem nenhum dado do arquivo ou da pessoa: só o evento, a origem e o status.
 * Falha de negócio esperada (4xx) é warning; indisponibilidade ou erro inesperado (0 ou 5xx) é error.
 */
export function logFailure(logger: AppLogger, event: string, failure: ApiFailure): void {
  const context = { source: LOGGER_SOURCE, status: failure.status };
  if (failure.status === 0 || failure.status >= 500) {
    logger.error(event, context);
  } else {
    logger.warning(event, context);
  }
}
