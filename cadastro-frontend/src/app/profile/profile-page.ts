import { HttpErrorResponse, httpResource, HttpStatusCode } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject } from '@angular/core';
import { AppLogger } from '../core/app-logger';
import {
  displayName,
  formatMobilePhone,
  formatPostalCode,
  initialsOf,
} from '../shared/person-format';
import { MyProfile } from './profile.model';

/** "Meu perfil" (US-06): os dados do usuário da sessão, lidos do Keycloak pelo BFF. */
@Component({
  selector: 'app-profile-page',
  templateUrl: './profile-page.html',
  styleUrl: './profile-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProfilePage {
  private readonly logger = inject(AppLogger);

  protected readonly profile = httpResource<MyProfile>(() => '/api/v1/users/me');
  protected readonly notInformed = $localize`Não informado`;

  protected readonly view = computed(() => {
    const profile = this.profile.hasValue() ? this.profile.value() : undefined;
    if (!profile) {
      return null;
    }
    const name = displayName(profile.firstName, profile.lastName, profile.userName);
    const address = profile.address;
    return {
      name,
      initials: initialsOf(name),
      userName: profile.userName,
      email: profile.email,
      mobilePhone: profile.mobilePhone ? formatMobilePhone(profile.mobilePhone) : null,
      address: address && {
        street: `${address.street}, ${address.number}`,
        complement: address.complement,
        cityLine: `${address.district}, ${address.city} - ${address.state}`,
        postalCode: formatPostalCode(address.postalCode),
      },
    };
  });

  constructor() {
    effect(() => {
      const error = this.profile.error();
      // 401 já leva ao login (interceptor); o resto é falha inesperada e vai ao log da API.
      if (
        error &&
        !(error instanceof HttpErrorResponse && error.status === HttpStatusCode.Unauthorized)
      ) {
        this.logger.error('profile.load_failed', { source: 'profile' }, error);
      }
    });
  }

  protected retry(): void {
    this.profile.reload();
  }
}
