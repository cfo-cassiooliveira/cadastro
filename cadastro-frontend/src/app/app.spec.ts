import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { App } from './app';
import { AuthService } from './auth/auth-service';
import { SessionUser } from './auth/auth.model';
import { BrowserNavigation } from './core/browser-navigation';

describe('App (shell)', () => {
  const user = signal<SessionUser>({
    isAuthenticated: false,
    userName: null,
    roles: [],
    antiforgeryToken: null,
  });
  const isAuthenticated = signal(false);
  const auth = {
    user,
    isAuthenticated,
    login: vi.fn(),
    logout: vi.fn().mockResolvedValue(undefined),
  };
  let currentPath = '/';
  let fixture: ComponentFixture<App>;
  let element: HTMLElement;

  async function render(): Promise<void> {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        { provide: BrowserNavigation, useValue: { currentPath: () => currentPath } },
      ],
    });
    fixture = TestBed.createComponent(App);
    element = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  }

  beforeEach(() => {
    currentPath = '/';
    isAuthenticated.set(false);
    user.set({ isAuthenticated: false, userName: null, roles: [], antiforgeryToken: null });
    auth.login.mockClear();
    auth.logout.mockClear();
  });

  it('mostra a marca e o atalho para o conteúdo', async () => {
    await render();

    expect(element.querySelector('header a.brand')?.getAttribute('href')).toBe('/');
    expect(element.querySelector('a.skip-link')?.getAttribute('href')).toBe('#main-content');
  });

  it('visitante vê "Entrar" e nenhum menu', async () => {
    await render();

    expect(element.querySelector('header button')?.textContent).toContain('Entrar');
    expect(element.querySelector('nav')).toBeNull();
  });

  it('quem entrou vê "Meu perfil", o login e "Sair"', async () => {
    isAuthenticated.set(true);
    user.set({
      isAuthenticated: true,
      userName: 'ana.souza',
      roles: ['user'],
      antiforgeryToken: 't',
    });
    await render();

    const nav = element.querySelector('nav')?.textContent ?? '';
    expect(nav).toContain('Meu perfil');
    expect(nav).toContain('ana.souza');

    element.querySelector<HTMLButtonElement>('nav button')?.click();
    expect(auth.logout).toHaveBeenCalledOnce();
  });

  it('o botão "Menu" abre e fecha o menu (aria-expanded)', async () => {
    isAuthenticated.set(true);
    await render();
    const toggle = element.querySelector<HTMLButtonElement>('.menu-toggle')!;

    toggle.click();
    await fixture.whenStable();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(element.querySelector('nav')?.classList).toContain('nav--open');

    toggle.click();
    await fixture.whenStable();
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
  });

  it.each([
    ['/?state=signed-out', 'Você saiu da sua conta.', 'status'],
    ['/profile?session=expired', 'Sua sessão expirou. Entre novamente.', 'status'],
    ['/?login=failed', 'Não foi possível entrar. Tente novamente.', 'alert'],
  ])('ao voltar com %s mostra o aviso e limpa a URL', async (path, text, role) => {
    currentPath = path;
    const navigate = vi.spyOn(Router.prototype, 'navigate').mockResolvedValue(true);
    await render();

    expect(element.querySelector(`[role="${role}"]`)?.textContent).toContain(text);
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({ replaceUrl: true }));
    navigate.mockRestore();
  });

  it('o aviso pode ser fechado', async () => {
    currentPath = '/?state=signed-out';
    await render();

    element.querySelector<HTMLButtonElement>('.alert__close')?.click();
    await fixture.whenStable();

    expect(element.querySelector('.alert')).toBeNull();
  });
});
