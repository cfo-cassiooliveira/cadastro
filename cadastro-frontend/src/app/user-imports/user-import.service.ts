import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom, Observable } from 'rxjs';
import {
  ApiFailure,
  ApiResult,
  ImportBatch,
  ImportLine,
  LineStatus,
  MAX_ISSUES_SHOWN,
  PAGE_SIZE,
  Paged,
  RowIssue,
  UploadSummary,
} from './user-import.model';

const BASE_URL = '/api/v1/user-imports';

/** Resposta de comandos que enfileiram mensagens (confirmar e reprocessar). */
export interface QueuedResponse {
  readonly messages: number;
}

/** Cliente da API de importação em lote. O antiforgery (`X-XSRF-TOKEN`) é anexado pelo interceptor nos POST. */
@Injectable({ providedIn: 'root' })
export class UserImportService {
  private readonly http = inject(HttpClient);

  /** Envia o CSV (multipart, campo `file`). A resposta 201 traz o resumo e o token de confirmação. */
  upload(file: File): Promise<ApiResult<UploadSummary>> {
    const body = new FormData();
    body.append('file', file, file.name);
    return this.call(this.http.post<UploadSummary>(BASE_URL, body));
  }

  confirm(batchId: string, summaryToken: string): Promise<ApiResult<QueuedResponse>> {
    return this.call(
      this.http.post<QueuedResponse>(`${BASE_URL}/${batchId}/confirm`, { summaryToken }),
    );
  }

  cancel(batchId: string): Promise<ApiResult<null>> {
    return this.call(this.http.post<null>(`${BASE_URL}/${batchId}/cancel`, null));
  }

  list(page: number): Promise<ApiResult<Paged<ImportBatch>>> {
    return this.call(
      this.http.get<Paged<ImportBatch>>(BASE_URL, { params: { page, pageSize: PAGE_SIZE } }),
    );
  }

  get(batchId: string): Promise<ApiResult<ImportBatch>> {
    return this.call(this.http.get<ImportBatch>(`${BASE_URL}/${batchId}`));
  }

  lines(
    batchId: string,
    page: number,
    status: LineStatus | null,
  ): Promise<ApiResult<Paged<ImportLine>>> {
    const params: Record<string, string | number> = { page, pageSize: PAGE_SIZE };
    if (status) {
      params['status'] = status;
    }
    return this.call(this.http.get<Paged<ImportLine>>(`${BASE_URL}/${batchId}/lines`, { params }));
  }

  reprocessErrors(batchId: string): Promise<ApiResult<QueuedResponse>> {
    return this.call(
      this.http.post<QueuedResponse>(`${BASE_URL}/${batchId}/reprocess-errors`, null),
    );
  }

  reprocessLine(batchId: string, lineId: string): Promise<ApiResult<QueuedResponse>> {
    return this.call(
      this.http.post<QueuedResponse>(`${BASE_URL}/${batchId}/lines/${lineId}/reprocess`, null),
    );
  }

  /** Relatório por linha (CSV sem dado pessoal). */
  report(batchId: string): Promise<ApiResult<Blob>> {
    return this.call(this.http.get(`${BASE_URL}/${batchId}/report`, { responseType: 'blob' }));
  }

  private async call<T>(request: Observable<T>): Promise<ApiResult<T>> {
    try {
      return { ok: true, value: await firstValueFrom(request) };
    } catch (error) {
      return { ok: false, failure: toFailure(error) };
    }
  }
}

function toFailure(error: unknown): ApiFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return { status: 0, code: null, batchId: null, issues: [] };
  }
  const body: unknown = error.error;
  if (typeof body !== 'object' || body === null || body instanceof Blob) {
    return { status: error.status, code: null, batchId: null, issues: [] };
  }
  const fields = body as Record<string, unknown>;
  return {
    status: error.status,
    code: typeof fields['code'] === 'string' ? fields['code'] : null,
    batchId: typeof fields['batchId'] === 'string' ? fields['batchId'] : null,
    issues: readIssues(fields['issues']),
  };
}

// Só pares {line, code} bem formados entram na tela; qualquer outro texto do corpo é ignorado.
function readIssues(value: unknown): RowIssue[] {
  if (!Array.isArray(value)) {
    return [];
  }
  return value
    .filter(
      (item): item is RowIssue => typeof item?.line === 'number' && typeof item?.code === 'string',
    )
    .slice(0, MAX_ISSUES_SHOWN);
}
