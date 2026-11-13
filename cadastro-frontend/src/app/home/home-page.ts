import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { AuthService } from '../auth/auth-service';

/** Início para visitante: o que é o Cadastro e o caminho para entrar (quem já entrou vai direto ao perfil). */
@Component({
  selector: 'app-home-page',
  templateUrl: './home-page.html',
  styleUrl: './home-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HomePage {
  private readonly auth = inject(AuthService);

  protected signIn(): void {
    this.auth.login('/profile');
  }
}
