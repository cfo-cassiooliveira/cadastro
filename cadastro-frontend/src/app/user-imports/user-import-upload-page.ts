import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  signal,
  viewChild,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth-service';
import { AppLogger } from '../core/app-logger';
import { LOGGER_SOURCE, logFailure } from './user-import.logging';
import { describeFailure, describeRowIssue, FailureMessage } from './user-import.messages';
import { ApiFailure, MAX_FILE_BYTES, RowIssue, UploadSummary } from './user-import.model';
import { UserImportService } from './user-import.service';

const ROUTE = '/imports/new';

/** Falha exibida no topo da tela, com os extras que algumas recusas trazem. */
interface Problem extends FailureMessage {
  readonly issues: readonly RowIssue[];
  readonly existingBatchId: string | null;
  /** O step-up recarrega a página: o arquivo escolhido no envio se perde. */
  readonly reselectFile: boolean;
}

/** Valida extensão e tamanho no cliente (a API confere de novo); devolve a mensagem ou null. */
export function validateFile(file: File | null): string | null {
  if (!file) {
    return $localize`Selecione um arquivo CSV.`;
  }
  if (!file.name.toLowerCase().endsWith('.csv')) {
    return $localize`O arquivo precisa ser um CSV (extensão .csv).`;
  }
  if (file.size === 0) {
    return $localize`O arquivo está vazio.`;
  }
  if (file.size > MAX_FILE_BYTES) {
    return $localize`O arquivo passa de 5 MB. Divida a lista em arquivos menores.`;
  }
  return null;
}

/**
 * Envio do CSV e resumo do lote (confirmar ou cancelar). O `summaryToken` vem só na resposta 201 e fica apenas neste
 * signal: não vai a storage, URL nem log.
 */
@Component({
  selector: 'app-user-import-upload-page',
  imports: [RouterLink],
  templateUrl: './user-import-upload-page.html',
  styleUrl: './user-import-upload-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserImportUploadPage {
  private readonly service = inject(UserImportService);
  private readonly auth = inject(AuthService);
  private readonly logger = inject(AppLogger);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('fileInput');
  private readonly problemBox = viewChild<ElementRef<HTMLElement>>('problemBox');
  private readonly summaryTitle = viewChild<ElementRef<HTMLElement>>('summaryTitle');

  protected readonly file = signal<File | null>(null);
  protected readonly fileError = signal<string | null>(null);
  protected readonly uploading = signal(false);
  protected readonly working = signal<'confirm' | 'cancel' | null>(null);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly summary = signal<UploadSummary | null>(null);
  protected readonly cancelled = signal(false);

  protected readonly fileLabel = computed(() => {
    const file = this.file();
    return file ? `${file.name} (${formatSize(file.size)})` : null;
  });
  protected readonly issueRows = computed(() =>
    (this.summary()?.issues ?? []).map((issue) => ({
      line: issue.line,
      text: describeRowIssue(issue.code),
    })),
  );
  protected readonly rejectedRows = computed(() =>
    (this.problem()?.issues ?? []).map((issue) => ({
      line: issue.line,
      text: describeRowIssue(issue.code),
    })),
  );
  protected readonly issueTotal = computed(() => {
    const summary = this.summary();
    return summary ? summary.invalidLines + summary.duplicateLines : 0;
  });

  protected selectFile(event: Event): void {
    const selected = (event.target as HTMLInputElement).files?.[0] ?? null;
    this.file.set(selected);
    this.fileError.set(selected ? validateFile(selected) : null);
    this.problem.set(null);
    this.cancelled.set(false);
  }

  protected async upload(event: Event): Promise<void> {
    event.preventDefault();
    if (this.uploading()) {
      return;
    }
    const file = this.file();
    const error = validateFile(file);
    this.fileError.set(error);
    if (error || !file) {
      this.focusAfterRender(this.fileInput);
      return;
    }
    this.problem.set(null);
    this.cancelled.set(false);
    this.uploading.set(true);
    const result = await this.service.upload(file);
    this.uploading.set(false);
    if (result.ok) {
      this.summary.set(result.value);
      this.focusAfterRender(this.summaryTitle);
      return;
    }
    this.showProblem('user_import.upload_failed', result.failure);
  }

  protected async confirm(): Promise<void> {
    const summary = this.summary();
    if (!summary || this.working()) {
      return;
    }
    this.problem.set(null);
    this.working.set('confirm');
    const result = await this.service.confirm(summary.batchId, summary.summaryToken);
    this.working.set(null);
    if (result.ok) {
      this.logger.info('user_import.confirmed', { source: LOGGER_SOURCE });
      // O token cumpriu o papel: sai da memória antes de ir ao acompanhamento.
      this.summary.set(null);
      await this.router.navigate(['/imports', summary.batchId]);
      return;
    }
    this.showProblem('user_import.confirm_failed', result.failure);
  }

  protected async cancel(): Promise<void> {
    const summary = this.summary();
    if (!summary || this.working()) {
      return;
    }
    this.problem.set(null);
    this.working.set('cancel');
    const result = await this.service.cancel(summary.batchId);
    this.working.set(null);
    if (result.ok) {
      this.resetToForm();
      this.cancelled.set(true);
      return;
    }
    this.showProblem('user_import.cancel_failed', result.failure);
  }

  /** Step-up: o BFF abre o login com OTP e traz o usuário de volta a esta tela. */
  protected confirmOtp(): void {
    this.auth.requestOtpStepUp(ROUTE);
  }

  private resetToForm(): void {
    this.summary.set(null);
    this.file.set(null);
    this.fileError.set(null);
    const input = this.fileInput()?.nativeElement;
    if (input) {
      input.value = '';
    }
  }

  private showProblem(event: string, failure: ApiFailure): void {
    if (failure.status === 401) {
      // O interceptor já leva ao login.
      return;
    }
    logFailure(this.logger, event, failure);
    const message = describeFailure(failure);
    this.problem.set({
      ...message,
      reselectFile: message.needsOtp && event === 'user_import.upload_failed',
      issues: failure.issues,
      existingBatchId: failure.code === 'import.duplicate_file' ? failure.batchId : null,
    });
    this.focusAfterRender(this.problemBox);
  }

  private focusAfterRender(target: () => ElementRef<HTMLElement> | undefined): void {
    afterNextRender(() => target()?.nativeElement.focus(), { injector: this.injector });
  }
}

function formatSize(bytes: number): string {
  const kb = bytes / 1024;
  const formatter = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 });
  return kb < 1024 ? `${formatter.format(kb)} KB` : `${formatter.format(kb / 1024)} MB`;
}
