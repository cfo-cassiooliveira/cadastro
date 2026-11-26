/** Sessão atual devolvida por `GET /bff/user` (sem token: o BFF guarda os tokens no servidor). */
export interface SessionUser {
  readonly isAuthenticated: boolean;
  readonly userName: string | null;
  readonly roles: readonly string[];
  /** Token anti-CSRF da sessão: fica só em memória e vai no header X-XSRF-TOKEN (nunca em cookie legível). */
  readonly antiforgeryToken: string | null;
}

/** Resposta de `POST /bff/logout`: para onde o navegador segue para encerrar o login no Keycloak. */
export interface LogoutResponse {
  readonly logoutUrl: string;
}

/** Marca na URL de volta do login: a tela mostra "Sua sessão expirou. Entre novamente.". */
export const SESSION_EXPIRED_PARAM = 'session';
export const SESSION_EXPIRED_VALUE = 'expired';

/** Role que libera a pesquisa em logs (o BFF ainda exige OTP recente: ver `AuthService.requestOtpStepUp`). */
export const LOG_INVESTIGATOR_ROLE = 'log-investigator';

/** Role que libera a importação em lote (o BFF ainda exige OTP recente nas chamadas). */
export const USER_IMPORTER_ROLE = 'user-importer';
