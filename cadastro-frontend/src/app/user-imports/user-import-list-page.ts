import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth-service';
import { AppLogger } from '../core/app-logger';
import { Pagination } from '../shared/pagination/pagination';
import { ImportProgress } from './import-progress/import-progress';
import { logFailure } from './user-import.logging';
import {
  batchStatusLabel,
  batchTone,
  describeFailure,
  FailureMessage,
} from './user-import.messages';
import { ImportBatch, PAGE_SIZE, Paged } from './user-import.model';
import { UserImportService } from './user-import.service';

const ROUTE = '/imports';

/** Lista de lotes (mais recentes primeiro), com estado e progresso por lote. */
@Component({
  selector: 'app-user-import-list-page',
  imports: [DatePipe, RouterLink, ImportProgress, Pagination],
  templateUrl: './user-import-list-page.html',
  styleUrl: './user-import-list-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserImportListPage {
  private readonly service = inject(UserImportService);
  private readonly auth = inject(AuthService);
  private readonly logger = inject(AppLogger);

  protected readonly pageSize = PAGE_SIZE;
  protected readonly page = signal(1);
  protected readonly loading = signal(true);
  protected readonly data = signal<Paged<ImportBatch> | null>(null);
  protected readonly problem = signal<FailureMessage | null>(null);

  protected readonly statusLabel = batchStatusLabel;
  protected readonly tone = batchTone;

  constructor() {
    void this.load(1);
  }

  protected async load(page: number): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);
    const result = await this.service.list(page);
    this.loading.set(false);
    if (result.ok) {
      this.page.set(page);
      this.data.set(result.value);
      return;
    }
    if (result.failure.status !== 401) {
      logFailure(this.logger, 'user_import.list_failed', result.failure);
      this.problem.set(describeFailure(result.failure));
    }
  }

  protected retry(): Promise<void> {
    return this.load(this.page());
  }

  /** Step-up: o BFF abre o login com OTP e traz o usuário de volta a esta tela. */
  protected confirmOtp(): void {
    this.auth.requestOtpStepUp(ROUTE);
  }
}
