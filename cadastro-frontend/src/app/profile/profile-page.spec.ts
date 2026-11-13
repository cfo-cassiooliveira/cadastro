import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { createLoggerSpy, LoggerSpy, provideLoggerSpy } from '../core/app-logger.testing';
import { ProfilePage } from './profile-page';
import { MyProfile } from './profile.model';

const FULL_PROFILE: MyProfile = {
  userName: 'ana.souza',
  firstName: 'Ana',
  lastName: 'Souza',
  email: 'ana.souza@example.com',
  mobilePhone: '+5511912345678',
  address: {
    postalCode: '01001000',
    street: 'Praça da Sé',
    number: '100',
    complement: 'Apto 12',
    district: 'Sé',
    city: 'São Paulo',
    state: 'SP',
  },
};

describe('ProfilePage', () => {
  let fixture: ComponentFixture<ProfilePage>;
  let element: HTMLElement;
  let backend: HttpTestingController;
  let logger: LoggerSpy;

  beforeEach(async () => {
    logger = createLoggerSpy();
    TestBed.configureTestingModule({
      imports: [ProfilePage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideLoggerSpy(logger)],
    });
    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ProfilePage);
    element = fixture.nativeElement as HTMLElement;
    // Com a requisição pendente a aplicação não fica "estável": só roda a detecção e os efeitos (dispara o GET).
    TestBed.tick();
  });

  async function respond(body: MyProfile | null, status = 200): Promise<void> {
    backend
      .expectOne('/api/v1/users/me')
      .flush(body, { status, statusText: status === 200 ? 'OK' : 'Error' });
    await fixture.whenStable();
  }

  it('mostra o esqueleto e anuncia o carregamento', () => {
    expect(element.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(element.querySelector('[role="status"]')?.textContent).toContain(
      'Carregando seu perfil',
    );
    backend.expectOne('/api/v1/users/me');
  });

  it('mostra nome, login, contato e endereço formatados', async () => {
    await respond(FULL_PROFILE);

    const text = element.textContent ?? '';
    expect(element.querySelector('h2')?.textContent).toBe('Ana Souza');
    expect(element.querySelector('.id-card__monogram')?.textContent).toBe('AS');
    expect(text).toContain('ana.souza');
    expect(text).toContain('ana.souza@example.com');
    expect(text).toContain('(11) 91234-5678');
    expect(text).toContain('Praça da Sé, 100');
    expect(text).toContain('Apto 12');
    expect(text).toContain('Sé, São Paulo - SP');
    expect(text).toContain('01001-000');
  });

  it('sem endereço e sem celular, mostra os textos de vazio', async () => {
    await respond({
      ...FULL_PROFILE,
      firstName: null,
      lastName: null,
      mobilePhone: null,
      address: null,
    });

    expect(element.querySelector('h2')?.textContent).toBe('ana.souza');
    expect(element.textContent).toContain('Endereço não informado.');
    expect(element.textContent).toContain('Não informado');
    expect(element.textContent).not.toMatch(/null|undefined/);
  });

  it('em erro, explica, registra e permite tentar de novo', async () => {
    await respond(null, 503);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain(
      'Não foi possível carregar seu perfil.',
    );
    expect(logger.error).toHaveBeenCalledWith(
      'profile.load_failed',
      { source: 'profile' },
      expect.anything(),
    );

    element.querySelector<HTMLButtonElement>('[role="alert"] button')?.click();
    TestBed.tick();
    await respond(FULL_PROFILE);

    expect(element.querySelector('h2')?.textContent).toBe('Ana Souza');
  });

  it('401 não é registrado como erro (o interceptor leva ao login)', async () => {
    await respond(null, 401);

    expect(logger.error).not.toHaveBeenCalled();
  });
});
