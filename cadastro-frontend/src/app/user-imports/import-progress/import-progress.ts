import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { LineCounts } from '../user-import.model';

type SegmentKey = 'created' | 'already-exists' | 'error' | 'pending' | 'cancelled';

interface Segment {
  readonly key: SegmentKey;
  readonly label: string;
  readonly count: number;
  readonly percent: number;
}

/** Progresso do lote: barra empilhada por estado, resumo "X de Y" e legenda com as contagens (a cor nunca é a única pista). */
@Component({
  selector: 'app-import-progress',
  templateUrl: './import-progress.html',
  styleUrl: './import-progress.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ImportProgress {
  readonly counts = input.required<LineCounts>();
  /** Anuncia mudanças do resumo a leitores de tela (só no detalhe, que atualiza por polling). */
  readonly live = input(false);

  protected readonly total = computed(() => {
    const c = this.counts();
    return c.created + c.alreadyExists + c.error + c.pending + c.cancelled;
  });
  protected readonly processed = computed(() => {
    const c = this.counts();
    return c.created + c.alreadyExists + c.error;
  });
  protected readonly segments = computed<readonly Segment[]>(() => {
    const c = this.counts();
    const total = this.total();
    const make = (key: SegmentKey, label: string, count: number): Segment => ({
      key,
      label,
      count,
      percent: total === 0 ? 0 : (count / total) * 100,
    });
    return [
      make('created', $localize`Criadas`, c.created),
      make('already-exists', $localize`Já existiam`, c.alreadyExists),
      make('error', $localize`Com erro`, c.error),
      make('pending', $localize`Na fila`, c.pending),
      make('cancelled', $localize`Canceladas`, c.cancelled),
    ];
  });
}
