import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Acesso negado: o usuário entrou, mas não tem a role que a página exige. */
@Component({
  selector: 'app-forbidden-page',
  imports: [RouterLink],
  templateUrl: './forbidden-page.html',
  styleUrl: './forbidden-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ForbiddenPage {}
