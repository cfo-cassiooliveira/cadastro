import { Provider } from '@angular/core';
import { AppLogger } from './app-logger';

/** Espião do AppLogger para os testes verificarem o nível de cada evento (regra de log). */
export interface LoggerSpy {
  readonly error: ReturnType<typeof vi.fn>;
  readonly warning: ReturnType<typeof vi.fn>;
  readonly info: ReturnType<typeof vi.fn>;
}

export function createLoggerSpy(): LoggerSpy {
  return { error: vi.fn(), warning: vi.fn(), info: vi.fn() };
}

export function provideLoggerSpy(spy: LoggerSpy): Provider {
  return { provide: AppLogger, useValue: spy };
}
