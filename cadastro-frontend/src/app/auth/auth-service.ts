import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppLogger } from '../core/app-logger';
import { BrowserNavigation } from '../core/browser-navigation';
import { LogoutResponse, SessionUser } from './auth.model';

const VISITOR: SessionUser = {
  isAuthenticated: false,
  userName: null,
  roles: [],
  antiforgeryToken: null,
};

/**
 * Sessão do usuário no BFF. O Angular nunca vê token OAuth: só sabe se há sessão, o login, as roles e o token
 * anti-CSRF (em memória), e manda o navegador ao BFF para entrar e sair.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly navigation = inject(BrowserNavigation);
  private readonly logger = inject(AppLogger);
  private readonly session = signal<SessionUser>(VISITOR);

  readonly user = this.session.asReadonly();
  readonly isAuthenticated = computed(() => this.session().isAuthenticated);
  readonly isAdmin = computed(() => this.session().roles.includes('admin'));
  readonly antiforgeryToken = computed(() => this.session().antiforgeryToken);

  /** Lê a sessão e o token anti-CSRF no BFF. Falha → trata como visitante. */
  async load(): Promise<void> {
    try {
      this.session.set(await firstValueFrom(this.http.get<SessionUser>('/bff/user')));
    } catch (error) {
      this.logger.error('auth.session_load_failed', { source: 'auth' }, error);
      this.session.set(VISITOR);
    }
  }

  /** Vai para o login do Keycloak (pelo BFF) e volta para `returnUrl` depois. */
  login(returnUrl = '/'): void {
    this.navigation.assign(`/bff/login?returnUrl=${encodeURIComponent(returnUrl)}`);
  }

  /** Encerra a sessão no BFF e segue para o Keycloak encerrar o login (volta com "Você saiu da sua conta."). */
  async logout(): Promise<void> {
    try {
      const response = await firstValueFrom(this.http.post<LogoutResponse>('/bff/logout', null));
      this.session.set(VISITOR);
      this.logger.info('auth.signed_out', { source: 'auth' });
      this.navigation.assign(response.logoutUrl);
    } catch (error) {
      this.logger.error('auth.logout_failed', { source: 'auth' }, error);
      // Sessão local pode já ter acabado: recarrega o estado em vez de deixar o menu errado na tela.
      await this.load();
    }
  }
}
