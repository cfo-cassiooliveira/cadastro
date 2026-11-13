import { TestBed } from '@angular/core/testing';
import { createLoggerSpy, LoggerSpy, provideLoggerSpy } from './app-logger.testing';
import { DEV_MODE, GlobalErrorHandler } from './global-error-handler';

describe('GlobalErrorHandler', () => {
  let logger: LoggerSpy;
  let consoleError: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    logger = createLoggerSpy();
    consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    TestBed.configureTestingModule({ providers: [GlobalErrorHandler, provideLoggerSpy(logger)] });
  });

  afterEach(() => vi.restoreAllMocks());

  it('registra todo erro não tratado como error', () => {
    const error = new Error('boom');

    TestBed.inject(GlobalErrorHandler).handleError(error);

    expect(logger.error).toHaveBeenCalledWith('app.unhandled_error', { source: 'global' }, error);
  });

  it('em desenvolvimento também mostra o erro no console', () => {
    vi.spyOn(DEV_MODE, 'enabled').mockReturnValue(true);
    const error = new Error('boom');

    TestBed.inject(GlobalErrorHandler).handleError(error);

    expect(consoleError).toHaveBeenCalledWith(error);
  });

  it('em produção não escreve nada no console', () => {
    vi.spyOn(DEV_MODE, 'enabled').mockReturnValue(false);

    TestBed.inject(GlobalErrorHandler).handleError(new Error('boom'));

    expect(consoleError).not.toHaveBeenCalled();
  });
});
