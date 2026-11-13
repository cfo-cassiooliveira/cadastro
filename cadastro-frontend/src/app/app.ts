import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  OnInit,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { AuthService } from './auth/auth-service';
import { SESSION_EXPIRED_PARAM, SESSION_EXPIRED_VALUE } from './auth/auth.model';
import { BrowserNavigation } from './core/browser-navigation';

/** Aviso exibido no topo ao voltar do Keycloak (logout, falha de login ou sessão expirada). */
interface ReturnNotice {
  readonly text: string;
  readonly tone: 'success' | 'info' | 'error';
}

/** Parâmetros de retorno que viram aviso e saem da URL logo depois de lidos. */
const RETURN_PARAMS = ['state', 'login', SESSION_EXPIRED_PARAM] as const;

/** Shell da aplicação: atalho para o conteúdo, cabeçalho com menu conforme a sessão, avisos de retorno e conteúdo. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly menuOpen = signal(false);
  protected readonly notice = signal<ReturnNotice | null>(
    readReturnNotice(inject(BrowserNavigation).currentPath()),
  );

  constructor() {
    // Menu do mobile fecha ao trocar de página.
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe(() => this.menuOpen.set(false));
  }

  /** O aviso já foi lido: tira os parâmetros de retorno da URL (recarregar não repete o aviso). */
  ngOnInit(): void {
    if (this.notice()) {
      const queryParams = Object.fromEntries(RETURN_PARAMS.map((name) => [name, null]));
      void this.router.navigate([], {
        queryParams,
        queryParamsHandling: 'merge',
        replaceUrl: true,
      });
    }
  }

  protected toggleMenu(): void {
    this.menuOpen.update((open) => !open);
  }

  protected signIn(): void {
    this.auth.login(this.router.url === '/' ? '/profile' : this.router.url);
  }

  protected signOut(): Promise<void> {
    return this.auth.logout();
  }

  protected dismissNotice(): void {
    this.notice.set(null);
  }
}

function readReturnNotice(path: string): ReturnNotice | null {
  const params = new URLSearchParams(path.split('?')[1] ?? '');
  if (params.get('state') === 'signed-out') {
    return { text: $localize`Você saiu da sua conta.`, tone: 'success' };
  }
  if (params.get(SESSION_EXPIRED_PARAM) === SESSION_EXPIRED_VALUE) {
    return { text: $localize`Sua sessão expirou. Entre novamente.`, tone: 'info' };
  }
  if (params.get('login') === 'failed') {
    return { text: $localize`Não foi possível entrar. Tente novamente.`, tone: 'error' };
  }
  return null;
}
