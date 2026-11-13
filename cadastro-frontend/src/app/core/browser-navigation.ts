import { Injectable } from '@angular/core';

/**
 * Navegação de página inteira (sair da SPA para o BFF/Keycloak). Isolada num serviço para os testes não
 * recarregarem o navegador.
 */
@Injectable({ providedIn: 'root' })
export class BrowserNavigation {
  /** Leva o navegador para a URL (login, logout). */
  assign(url: string): void {
    window.location.assign(url);
  }

  /** Caminho atual com a query string, para voltar a ele depois do login. */
  currentPath(): string {
    return `${window.location.pathname}${window.location.search}`;
  }
}
