/** Tipos de dado aceitos pela API (`POST /api/v1/log-search/queries`). */
export type LogSearchDataType = 'cpf' | 'cnpj' | 'phone' | 'email' | 'login';

/** Campos do formulário que a API valida e devolve em `errors`. */
export type LogSearchField = 'dataType' | 'value' | 'caseReference';

/** Corpo da requisição. O valor só trafega aqui: nunca em URL, storage ou log. */
export interface LogSearchRequest {
  readonly dataType: LogSearchDataType;
  readonly value: string;
  readonly caseReference: string;
}

/** Token do valor para uma chave de pseudonimização vigente. */
export interface LogSearchToken {
  readonly keyId: string;
  readonly token: string;
}

/** Resposta 200: tokens por chave e a consulta pronta para colar no Graylog. */
export interface LogSearchResult {
  readonly dataType: LogSearchDataType;
  readonly tokens: readonly LogSearchToken[];
  readonly graylogQuery: string;
}

/** Códigos de erro por campo devolvidos pela API (sem eco do valor). */
export type LogSearchErrorCode =
  | 'log_search.required'
  | 'log_search.invalid_value'
  | 'log_search.invalid_data_type'
  | 'log_search.invalid_case_reference';

/** Resultado de uma tentativa de pesquisa, já classificado para a tela. */
export type LogSearchOutcome =
  | { readonly kind: 'success'; readonly result: LogSearchResult }
  | {
      readonly kind: 'invalid';
      readonly errors: Partial<Record<LogSearchField, LogSearchErrorCode>>;
    }
  | { readonly kind: 'forbidden' }
  | { readonly kind: 'rate-limited' }
  | { readonly kind: 'unauthorized' }
  | { readonly kind: 'failed'; readonly status: number };
