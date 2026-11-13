import { TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth-service';
import { HomePage } from './home-page';

describe('HomePage', () => {
  const login = vi.fn();
  let element: HTMLElement;

  beforeEach(async () => {
    login.mockClear();
    TestBed.configureTestingModule({
      imports: [HomePage],
      providers: [{ provide: AuthService, useValue: { login } }],
    });
    const fixture = TestBed.createComponent(HomePage);
    element = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  it('apresenta o Cadastro com um único título', () => {
    expect(element.querySelectorAll('h1')).toHaveLength(1);
    expect(element.querySelector('h1')?.textContent).toContain(
      'Seu cadastro do Workspace em um só lugar',
    );
  });

  it('"Entrar" leva ao login e volta para o perfil', () => {
    element.querySelector<HTMLButtonElement>('.home__actions button')?.click();

    expect(login).toHaveBeenCalledWith('/profile');
  });

  it('a ilustração do cartão fica fora da leitura de tela', () => {
    expect(element.querySelector('.home__art')?.getAttribute('aria-hidden')).toBe('true');
  });
});
