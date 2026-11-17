import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  Injector,
  signal,
  viewChild,
  ElementRef,
} from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../auth/auth-service';
import { LOG_INVESTIGATOR_ROLE } from '../auth/auth.model';
import { AppLogger } from '../core/app-logger';
import {
  LogSearchDataType,
  LogSearchField,
  LogSearchOutcome,
  LogSearchResult,
} from './log-search.model';
import { LogSearchService } from './log-search.service';
import {
  DATA_TYPE_OPTIONS,
  validateCaseReference,
  validateValue,
  valueHint,
} from './log-search.validation';

/** Aviso geral da tela (fora da validação por campo). */
type Problem = 'failed' | 'rate-limited' | 'step-up';

type FieldMessages = Partial<Record<'value' | 'caseReference', string>>;

const LOGGER_SOURCE = 'log-search';
const ROUTE = '/log-search';
const INPUT_MODES: Record<LogSearchDataType, string> = {
  cpf: 'numeric',
  cnpj: 'text',
  phone: 'tel',
  email: 'email',
  login: 'text',
};

/**
 * "Pesquisa em logs": gera a consulta do Graylog pelos tokens de um dado pessoal. O valor digitado vive só no campo
 * até o envio, é apagado após o sucesso e nunca vai para URL, storage, AppLogger ou console.
 */
@Component({
  selector: 'app-log-search-page',
  templateUrl: './log-search-page.html',
  styleUrl: './log-search-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LogSearchPage {
  private readonly service = inject(LogSearchService);
  private readonly auth = inject(AuthService);
  private readonly logger = inject(AppLogger);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  private readonly valueInput = viewChild<ElementRef<HTMLInputElement>>('valueInput');
  private readonly referenceInput = viewChild<ElementRef<HTMLInputElement>>('referenceInput');
  private readonly resultTitle = viewChild<ElementRef<HTMLElement>>('resultTitle');
  private readonly problemBox = viewChild<ElementRef<HTMLElement>>('problemBox');

  protected readonly dataTypes = DATA_TYPE_OPTIONS;
  protected readonly dataType = signal<LogSearchDataType>('cpf');
  protected readonly value = signal('');
  protected readonly caseReference = signal('');
  protected readonly fieldErrors = signal<FieldMessages>({});
  protected readonly loading = signal(false);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly result = signal<LogSearchResult | null>(null);
  protected readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');

  protected readonly hint = computed(() => valueHint(this.dataType()));
  protected readonly inputMode = computed(() => INPUT_MODES[this.dataType()]);
  protected readonly resultTypeLabel = computed(
    () => DATA_TYPE_OPTIONS.find((option) => option.id === this.result()?.dataType)?.label ?? '',
  );

  protected selectType(event: Event): void {
    const selected = (event.target as HTMLSelectElement).value;
    const option = DATA_TYPE_OPTIONS.find((candidate) => candidate.id === selected);
    if (!option) {
      return;
    }
    this.dataType.set(option.id);
    this.fieldErrors.update(({ caseReference }) => ({ caseReference }));
  }

  protected typeValue(event: Event): void {
    this.value.set((event.target as HTMLInputElement).value);
  }

  protected typeReference(event: Event): void {
    this.caseReference.set((event.target as HTMLInputElement).value);
  }

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (this.loading()) {
      return;
    }
    const errors: FieldMessages = {
      value: validateValue(this.dataType(), this.value()) ?? undefined,
      caseReference: validateCaseReference(this.caseReference().trim()) ?? undefined,
    };
    this.fieldErrors.set(errors);
    if (errors.value || errors.caseReference) {
      this.focusFirstInvalid(errors);
      return;
    }
    this.problem.set(null);
    this.result.set(null);
    this.copyState.set('idle');
    this.loading.set(true);
    const outcome = await this.service.search({
      dataType: this.dataType(),
      value: this.value(),
      caseReference: this.caseReference().trim(),
    });
    this.loading.set(false);
    this.handle(outcome);
  }

  protected retry(event: Event): Promise<void> {
    return this.submit(event);
  }

  protected async copyQuery(): Promise<void> {
    const query = this.result()?.graylogQuery;
    if (!query) {
      return;
    }
    try {
      await navigator.clipboard.writeText(query);
      this.copyState.set('copied');
    } catch {
      this.copyState.set('failed');
    }
  }

  /** Volta ao formulário vazio para outra pesquisa (tipo e referência do caso ficam). */
  protected startOver(): void {
    this.result.set(null);
    this.copyState.set('idle');
    this.value.set('');
    afterNextRender(() => this.valueInput()?.nativeElement.focus(), { injector: this.injector });
  }

  /** Step-up: o BFF abre o login com OTP e traz o usuário de volta a esta tela. */
  protected confirmOtp(): void {
    // Zera estado e campos (inclusive no DOM, antes da navegação) para o bfcache não restaurar o valor no "voltar".
    this.value.set('');
    this.caseReference.set('');
    const inputs = [this.valueInput(), this.referenceInput()];
    for (const input of inputs) {
      if (input) {
        input.nativeElement.value = '';
      }
    }
    this.auth.requestOtpStepUp(ROUTE);
  }

  private handle(outcome: LogSearchOutcome): void {
    switch (outcome.kind) {
      case 'success':
        // O valor sai da tela e do estado assim que a consulta existe.
        this.value.set('');
        this.result.set(outcome.result);
        this.focusAfterRender(this.resultTitle);
        return;
      case 'invalid':
        this.showServerErrors(outcome.errors);
        return;
      case 'forbidden':
        this.handleForbidden();
        return;
      case 'rate-limited':
        this.logger.warning('log_search.rate_limited', { source: LOGGER_SOURCE, status: 429 });
        this.showProblem('rate-limited');
        return;
      case 'unauthorized':
        // O interceptor já leva ao login.
        return;
      case 'failed':
        this.logger.error('log_search.failed', { source: LOGGER_SOURCE, status: outcome.status });
        this.showProblem('failed');
        return;
    }
  }

  // A API só devolve 403 com corpo vazio: com a role (o guard garante), a causa é o OTP ausente ou vencido (5 min).
  private handleForbidden(): void {
    if (this.auth.hasRole(LOG_INVESTIGATOR_ROLE)) {
      this.showProblem('step-up');
    } else {
      void this.router.navigateByUrl('/forbidden');
    }
  }

  private showServerErrors(errors: Partial<Record<LogSearchField, string>>): void {
    const messages: FieldMessages = {
      value: errors.value
        ? (validateValue(this.dataType(), this.value()) ?? $localize`Revise o valor informado.`)
        : undefined,
      caseReference: errors.caseReference
        ? (validateCaseReference(this.caseReference().trim()) ??
          $localize`Revise a referência do caso.`)
        : undefined,
    };
    if (errors.dataType) {
      this.showProblem('failed');
      return;
    }
    this.fieldErrors.set(messages);
    this.focusFirstInvalid(messages);
  }

  private showProblem(problem: Problem): void {
    this.problem.set(problem);
    this.focusAfterRender(this.problemBox);
  }

  private focusFirstInvalid(errors: FieldMessages): void {
    this.focusAfterRender(errors.value ? this.valueInput : this.referenceInput);
  }

  private focusAfterRender(target: () => ElementRef<HTMLElement> | undefined): void {
    afterNextRender(() => target()?.nativeElement.focus(), { injector: this.injector });
  }
}
