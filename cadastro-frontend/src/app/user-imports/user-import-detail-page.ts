import { DatePipe } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  Injector,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth-service';
import { AppLogger } from '../core/app-logger';
import { Pagination } from '../shared/pagination/pagination';
import { saveBlob } from '../shared/save-blob';
import { ImportProgress } from './import-progress/import-progress';
import { logFailure } from './user-import.logging';
import {
  batchStatusLabel,
  batchTone,
  describeFailure,
  describeLegacyMark,
  isLineReprocessable,
  describeLineFailure,
  FailureMessage,
  lineStatusLabel,
  lineTone,
} from './user-import.messages';
import {
  ApiFailure,
  ImportBatch,
  ImportLine,
  LineStatus,
  PAGE_SIZE,
  Paged,
} from './user-import.model';
import { UserImportService } from './user-import.service';

/** Intervalo do acompanhamento enquanto o lote está em processamento (decisão Q2: polling, sem SignalR no MVP). */
export const POLL_INTERVAL_MS = 3000;

/** Teto do intervalo quando as consultas falham (3 s, 6 s, 12 s, 24 s, 30 s). */
export const POLL_MAX_INTERVAL_MS = 30_000;

/** Próximo intervalo do polling: dobra a cada falha (até o teto) e volta a 3 s no primeiro sucesso. */
export function nextPollDelay(current: number, succeeded: boolean): number {
  return succeeded ? POLL_INTERVAL_MS : Math.min(current * 2, POLL_MAX_INTERVAL_MS);
}

/** Filtros da lista de linhas (valor vazio = todas). */
const LINE_FILTERS: readonly { readonly value: LineStatus | ''; readonly label: string }[] = [
  { value: '', label: $localize`Todas` },
  { value: 'Error', label: $localize`Com erro` },
  { value: 'Pending', label: $localize`Na fila` },
  { value: 'Created', label: $localize`Criadas` },
  { value: 'AlreadyExists', label: $localize`Já existiam` },
];

/**
 * Detalhe do lote: progresso, linhas paginadas (estado e motivo, sem dado pessoal), reprocessamento e relatório. Enquanto
 * o lote processa, atualiza a cada 3 s; o intervalo é desfeito ao concluir e ao sair da tela.
 */
@Component({
  selector: 'app-user-import-detail-page',
  imports: [DatePipe, RouterLink, ImportProgress, Pagination],
  templateUrl: './user-import-detail-page.html',
  styleUrl: './user-import-detail-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserImportDetailPage {
  /** Id do lote, vindo da rota (`withComponentInputBinding`). */
  readonly id = input.required<string>();

  private readonly service = inject(UserImportService);
  private readonly auth = inject(AuthService);
  private readonly logger = inject(AppLogger);
  private readonly injector = inject(Injector);

  private readonly problemBox = viewChild<ElementRef<HTMLElement>>('problemBox');

  protected readonly pageSize = PAGE_SIZE;
  protected readonly filters = LINE_FILTERS;
  protected readonly batch = signal<ImportBatch | null>(null);
  protected readonly loading = signal(true);
  protected readonly lines = signal<Paged<ImportLine> | null>(null);
  protected readonly linesLoading = signal(false);
  protected readonly linesPage = signal(1);
  protected readonly filter = signal<LineStatus | ''>('');
  protected readonly problem = signal<FailureMessage | null>(null);
  protected readonly notice = signal<{
    readonly text: string;
    readonly tone: 'success' | 'info';
  } | null>(null);
  protected readonly busy = signal<'reprocess' | 'cancel' | 'report' | null>(null);
  protected readonly reprocessingLine = signal<string | null>(null);

  protected readonly processing = computed(() => this.batch()?.status === 'Processing');
  protected readonly showLines = computed(() => {
    const status = this.batch()?.status;
    return status !== undefined && status !== 'AwaitingConfirmation' && status !== 'Cancelled';
  });
  protected readonly canReprocess = computed(() => {
    const batch = this.batch();
    return !!batch && batch.progress.error > 0 && batch.status !== 'Processing';
  });

  protected readonly statusLabel = batchStatusLabel;
  protected readonly tone = batchTone;
  protected readonly lineLabel = lineStatusLabel;
  protected readonly lineToneOf = lineTone;
  protected readonly lineReason = (line: ImportLine): string =>
    line.status === 'Error' ? describeLineFailure(line.failureReason) : '';
  protected readonly canReprocessLine = (line: ImportLine): boolean =>
    line.status === 'Error' && isLineReprocessable(line.failureReason);
  protected readonly legacyNote = (line: ImportLine): string | null =>
    line.status === 'Error' ? null : describeLegacyMark(line.legacyMarkStatus);

  private polling = false;
  private pollFailed = false;
  private linesRequest = 0;

  constructor() {
    // O id chega por input depois do construtor; trocar de lote (mesma rota) recarrega a tela.
    effect(() => {
      this.id();
      untracked(() => void this.loadAll());
    });
    // Polling só enquanto processa: cada ciclo agenda o próximo (com recuo se falhar); concluir ou destruir a tela
    // desfaz o temporizador pendente.
    effect((onCleanup) => {
      if (!this.processing()) {
        return;
      }
      let delay = POLL_INTERVAL_MS;
      let stopped = false;
      let handle: ReturnType<typeof setTimeout> | undefined;
      const schedule = (): void => {
        handle = setTimeout(() => {
          void this.poll().then((succeeded) => {
            delay = nextPollDelay(delay, succeeded);
            if (!stopped && this.processing()) {
              schedule();
            }
          });
        }, delay);
      };
      schedule();
      onCleanup(() => {
        stopped = true;
        clearTimeout(handle);
      });
    });
  }

  protected async loadAll(): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);
    this.batch.set(null);
    this.lines.set(null);
    this.notice.set(null);
    const ok = await this.refreshBatch();
    this.loading.set(false);
    if (ok && this.showLines()) {
      await this.loadLines(1);
    }
  }

  protected async loadLines(page: number): Promise<boolean> {
    const request = ++this.linesRequest;
    this.linesLoading.set(true);
    const result = await this.service.lines(this.id(), page, this.filter() || null);
    if (request !== this.linesRequest) {
      return true;
    }
    this.linesLoading.set(false);
    if (result.ok) {
      this.linesPage.set(page);
      this.lines.set(result.value);
      return true;
    }
    this.fail('user_import.lines_failed', result.failure);
    return false;
  }

  protected selectFilter(event: Event): void {
    this.filter.set((event.target as HTMLSelectElement).value as LineStatus | '');
    void this.loadLines(1);
  }

  protected async reprocessErrors(): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set('reprocess');
    const result = await this.service.reprocessErrors(this.id());
    this.busy.set(null);
    if (result.ok) {
      this.announce(
        result.value.messages > 0
          ? $localize`Linhas com erro reenviadas para a fila de processamento: ${result.value.messages}:COUNT:.`
          : $localize`Nenhuma linha pôde ser reprocessada: os dados delas não estão mais disponíveis. Envie as pessoas de novo em um novo arquivo.`,
        result.value.messages > 0 ? 'success' : 'info',
      );
      await this.refreshAfterCommand();
      return;
    }
    this.fail('user_import.reprocess_failed', result.failure);
  }

  protected async reprocessLine(line: ImportLine): Promise<void> {
    if (this.reprocessingLine()) {
      return;
    }
    this.reprocessingLine.set(line.id);
    const result = await this.service.reprocessLine(this.id(), line.id);
    this.reprocessingLine.set(null);
    if (result.ok) {
      this.announce(
        $localize`A linha ${line.lineNumber}:LINE: voltou para a fila de processamento.`,
      );
      await this.refreshAfterCommand();
      return;
    }
    this.fail('user_import.reprocess_line_failed', result.failure);
  }

  protected async cancel(): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set('cancel');
    const result = await this.service.cancel(this.id());
    this.busy.set(null);
    if (result.ok) {
      this.announce($localize`Lote cancelado. Nenhuma pessoa foi criada.`);
      await this.refreshBatch();
      return;
    }
    this.fail('user_import.cancel_failed', result.failure);
  }

  protected async downloadReport(): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set('report');
    const result = await this.service.report(this.id());
    this.busy.set(null);
    if (result.ok) {
      saveBlob(result.value, `relatorio-lote-${this.id()}.csv`);
      return;
    }
    this.fail('user_import.report_failed', result.failure);
  }

  /** Step-up: o BFF abre o login com OTP e traz o usuário de volta a esta tela. */
  protected confirmOtp(): void {
    this.auth.requestOtpStepUp(`/imports/${this.id()}`);
  }

  protected retry(): Promise<void> {
    return this.loadAll();
  }

  // Atualiza lote e página atual sem mostrar carregando; avisa quando o lote termina.
  private async poll(): Promise<boolean> {
    if (this.polling) {
      return true;
    }
    this.polling = true;
    try {
      const wasProcessing = this.processing();
      let succeeded = await this.refreshBatch();
      if (succeeded && this.showLines()) {
        succeeded = await this.loadLines(this.linesPage());
      }
      if (succeeded && this.pollFailed) {
        // A consulta voltou a funcionar: some o aviso de falha do próprio polling.
        this.problem.set(null);
      }
      this.pollFailed = !succeeded;
      const status = this.batch()?.status;
      if (wasProcessing && (status === 'Completed' || status === 'CompletedWithErrors')) {
        this.announce(
          status === 'Completed'
            ? $localize`Importação concluída. Todas as linhas foram processadas.`
            : $localize`Importação concluída com erros. Confira as linhas com erro e reprocesse.`,
          status === 'Completed' ? 'success' : 'info',
        );
      }
      return succeeded;
    } finally {
      this.polling = false;
    }
  }

  private async refreshAfterCommand(): Promise<void> {
    if ((await this.refreshBatch()) && this.showLines()) {
      await this.loadLines(this.linesPage());
    }
  }

  private async refreshBatch(): Promise<boolean> {
    const result = await this.service.get(this.id());
    if (result.ok) {
      this.batch.set(result.value);
      return true;
    }
    this.fail('user_import.get_failed', result.failure);
    return false;
  }

  private announce(text: string, tone: 'success' | 'info' = 'success'): void {
    this.notice.set({ text, tone });
    this.problem.set(null);
  }

  private fail(event: string, failure: ApiFailure): void {
    if (failure.status === 401) {
      // O interceptor já leva ao login.
      return;
    }
    logFailure(this.logger, event, failure);
    this.problem.set(describeFailure(failure));
    this.notice.set(null);
    afterNextRender(() => this.problemBox()?.nativeElement.focus(), { injector: this.injector });
  }
}
