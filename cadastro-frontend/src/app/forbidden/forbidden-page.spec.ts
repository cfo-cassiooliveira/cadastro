import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ForbiddenPage } from './forbidden-page';

describe('ForbiddenPage', () => {
  it('explica a falta de permissão e leva de volta ao perfil', async () => {
    TestBed.configureTestingModule({ imports: [ForbiddenPage], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(ForbiddenPage);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('h1')?.textContent).toBe('Acesso negado');
    expect(element.querySelector('[role="alert"]')?.textContent).toBe(
      'Você não tem permissão para acessar esta página.',
    );
    const link = element.querySelector('a');
    expect(link?.textContent).toBe('Voltar para meu perfil');
    expect(link?.getAttribute('href')).toBe('/profile');
  });
});
