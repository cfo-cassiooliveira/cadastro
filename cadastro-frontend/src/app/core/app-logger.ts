import { HttpBackend, HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CORRELATION_ID_HEADER } from './correlation-id.interceptor';

/** Níveis aceitos pela API de logs do navegador (regra de log do workspace). */
export type ClientLogLevel = 'error' | 'warning' | 'information';

/** Contexto do evento: de onde veio e, se houver, o status HTTP envolvido. */
export interface ClientLogContext {
  readonly source: string;
  readonly status?: number;
}

/** Corpo de `POST /api/v1/client-logs`. */
interface ClientLogPayload {
  readonly level: ClientLogLevel;
  readonly event: string;
  readonly message: string;
  readonly source: string;
  readonly status?: number;
  readonly stack?: string;
}

const CLIENT_LOGS_URL = '/api/v1/client-logs';
const MAX_MESSAGE_LENGTH = 300;
const MAX_STACK_LENGTH = 3000;

/**
 * Registra eventos do navegador na API (que os envia ao Graylog), para que falhas do frontend sejam rastreáveis.
 * Regra: exceção capturada → error; falha de negócio esperada → warning; operação concluída → info.
 * Nunca envie valores digitados pelo usuário: só códigos de evento, origem e status.
 */
@Injectable({ providedIn: 'root' })
export class AppLogger {
  // HttpBackend ignora os interceptors: uma falha ao logar não dispara outro log nem o tratamento de 401.
  private readonly http = new HttpClient(inject(HttpBackend));

  /** Exceção capturada ou falha inesperada (sempre error). */
  error(event: string, context: ClientLogContext, error?: unknown): void {
    const details = describe(error);
    this.send({
      level: 'error',
      event,
      source: context.source,
      status: context.status ?? details.status,
      message: details.message,
      stack: details.stack,
    });
  }

  /** Falha de negócio esperada ou aviso importante (credenciais, bloqueio, cadastro rejeitado...). */
  warning(event: string, context: ClientLogContext, message = ''): void {
    this.send({ level: 'warning', event, source: context.source, status: context.status, message });
  }

  /** Operação concluída como esperado (cadastro, login, logout). */
  info(event: string, context: ClientLogContext, message = ''): void {
    this.send({
      level: 'information',
      event,
      source: context.source,
      status: context.status,
      message,
    });
  }

  private send(payload: ClientLogPayload): void {
    const request = this.http.post<void>(CLIENT_LOGS_URL, payload, {
      headers: { [CORRELATION_ID_HEADER]: crypto.randomUUID() },
    });
    // Envio "fire and forget": logar nunca pode quebrar a tela nem gerar exceção em cascata.
    void firstValueFrom(request).catch(() => undefined);
  }
}

/** Extrai dados técnicos do erro sem URL nem corpo (podem conter CEP ou outros dados do usuário). */
function describe(error: unknown): { message: string; status?: number; stack?: string } {
  if (error instanceof HttpErrorResponse) {
    return { message: `HTTP ${error.status} ${error.statusText}`.trim(), status: error.status };
  }
  if (error instanceof Error) {
    return {
      message: `${error.name}: ${error.message}`.slice(0, MAX_MESSAGE_LENGTH),
      stack: error.stack?.slice(0, MAX_STACK_LENGTH),
    };
  }
  return { message: error === undefined ? '' : 'Non-error value thrown' };
}
