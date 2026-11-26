import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

/** Paginação anterior/próxima com a posição ("Página 2 de 5"); a página vive no componente pai. */
@Component({
  selector: 'app-pagination',
  templateUrl: './pagination.html',
  styleUrl: './pagination.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Pagination {
  readonly page = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly totalCount = input.required<number>();
  /** Rótulo acessível da navegação (ex.: "Paginação dos lotes"). */
  readonly label = input.required<string>();
  readonly pageChange = output<number>();

  protected readonly totalPages = computed(() =>
    Math.max(1, Math.ceil(this.totalCount() / this.pageSize())),
  );
  protected readonly hasPrevious = computed(() => this.page() > 1);
  protected readonly hasNext = computed(() => this.page() < this.totalPages());

  protected go(page: number): void {
    this.pageChange.emit(page);
  }
}
