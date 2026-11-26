/** Estado do lote (nome do enum da API). */
export type BatchStatus =
  'AwaitingConfirmation' | 'Processing' | 'Completed' | 'CompletedWithErrors' | 'Cancelled';

/** Estado de uma linha do lote (nome do enum da API). */
export type LineStatus = 'Pending' | 'Created' | 'AlreadyExists' | 'Error' | 'Cancelled';

/** Linhas por estado, para o progresso. */
export interface LineCounts {
  readonly pending: number;
  readonly created: number;
  readonly alreadyExists: number;
  readonly error: number;
  readonly cancelled: number;
}

/** Lote para a lista e o detalhe (sem dado pessoal). */
export interface ImportBatch {
  readonly id: string;
  readonly status: BatchStatus;
  readonly validLines: number;
  readonly invalidLines: number;
  readonly duplicateLines: number;
  readonly createdAtUtc: string;
  readonly confirmedAtUtc: string | null;
  readonly finishedAtUtc: string | null;
  readonly progress: LineCounts;
}

/** Linha do relatório: estado e motivo por código, nunca dado pessoal. */
export interface ImportLine {
  readonly id: string;
  readonly lineNumber: number;
  readonly status: LineStatus;
  readonly failureReason: string | null;
  readonly legacyMarkStatus: string;
  readonly reprocessCount: number;
}

/** Página de resultados. */
export interface Paged<T> {
  readonly items: readonly T[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
}

/** Problema encontrado em uma linha do CSV (número da linha e código). */
export interface RowIssue {
  readonly line: number;
  readonly code: string;
}

/** Resposta 201 do upload. O `summaryToken` só existe aqui e vive apenas em memória. */
export interface UploadSummary {
  readonly batchId: string;
  readonly validLines: number;
  readonly invalidLines: number;
  readonly duplicateLines: number;
  readonly summaryToken: string;
  readonly issues: readonly RowIssue[];
  readonly issuesTruncated: boolean;
}

/** Falha de uma chamada: status HTTP e o `code` do Problem Details (nunca texto do servidor nem dado pessoal). */
export interface ApiFailure {
  readonly status: number;
  readonly code: string | null;
  /** Lote já existente (409 `import.duplicate_file`). */
  readonly batchId: string | null;
  /** Problemas por linha da recusa 422 `import.no_valid_lines`. */
  readonly issues: readonly RowIssue[];
}

/** Resultado de uma chamada à API, já classificado para a tela. */
export type ApiResult<T> =
  { readonly ok: true; readonly value: T } | { readonly ok: false; readonly failure: ApiFailure };

/** Limites do arquivo (iguais aos da API; ela continua sendo a autoridade). */
export const MAX_FILE_BYTES = 5 * 1024 * 1024;
export const MAX_ISSUES_SHOWN = 200;
export const PAGE_SIZE = 20;
