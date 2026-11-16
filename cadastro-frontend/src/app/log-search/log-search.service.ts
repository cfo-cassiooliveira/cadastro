import { HttpClient, HttpErrorResponse, HttpStatusCode } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  LogSearchErrorCode,
  LogSearchField,
  LogSearchOutcome,
  LogSearchRequest,
  LogSearchResult,
} from './log-search.model';

const LOG_SEARCH_URL = '/api/v1/log-search/queries';
const FIELDS: readonly LogSearchField[] = ['dataType', 'value', 'caseReference'];

/** Cliente da API de pesquisa em logs. Classifica a resposta; não guarda nem registra o valor. */
@Injectable({ providedIn: 'root' })
export class LogSearchService {
  private readonly http = inject(HttpClient);

  /** Gera a consulta do Graylog. O antiforgery (`X-XSRF-TOKEN`) é anexado pelo interceptor. */
  async search(request: LogSearchRequest): Promise<LogSearchOutcome> {
    try {
      const result = await firstValueFrom(this.http.post<LogSearchResult>(LOG_SEARCH_URL, request));
      return { kind: 'success', result };
    } catch (error) {
      return classify(error);
    }
  }
}

function classify(error: unknown): LogSearchOutcome {
  if (!(error instanceof HttpErrorResponse)) {
    return { kind: 'failed', status: 0 };
  }
  switch (error.status) {
    case HttpStatusCode.BadRequest: {
      const errors = readFieldErrors(error.error);
      return Object.keys(errors).length > 0
        ? { kind: 'invalid', errors }
        : { kind: 'failed', status: error.status };
    }
    case HttpStatusCode.Unauthorized:
      return { kind: 'unauthorized' };
    case HttpStatusCode.Forbidden:
      return { kind: 'forbidden' };
    case HttpStatusCode.TooManyRequests:
      return { kind: 'rate-limited' };
    default:
      return { kind: 'failed', status: error.status };
  }
}

// Só os códigos `log_search.*` por campo são aceitos; qualquer outro texto do corpo é ignorado.
function readFieldErrors(body: unknown): Partial<Record<LogSearchField, LogSearchErrorCode>> {
  const errors = (body as { errors?: Record<string, unknown> } | null)?.errors;
  const result: Partial<Record<LogSearchField, LogSearchErrorCode>> = {};
  if (!errors || typeof errors !== 'object') {
    return result;
  }
  for (const field of FIELDS) {
    const codes = errors[field];
    const first: unknown = Array.isArray(codes) ? codes[0] : undefined;
    if (typeof first === 'string' && first.startsWith('log_search.')) {
      result[field] = first as LogSearchErrorCode;
    }
  }
  return result;
}
