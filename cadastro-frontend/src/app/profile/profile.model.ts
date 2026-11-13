/** Perfil devolvido por `GET /api/v1/users/me` (User Profile do Keycloak). */
export interface MyProfile {
  readonly userName: string;
  readonly firstName: string | null;
  readonly lastName: string | null;
  readonly email: string | null;
  readonly mobilePhone: string | null;
  readonly address: ProfileAddress | null;
}

/** Endereço residencial do perfil. */
export interface ProfileAddress {
  readonly postalCode: string;
  readonly street: string;
  readonly number: string;
  readonly complement: string | null;
  readonly district: string;
  readonly city: string;
  readonly state: string;
}
