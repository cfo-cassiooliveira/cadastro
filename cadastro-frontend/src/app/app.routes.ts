import { Routes } from '@angular/router';
import { authGuard, visitorGuard } from './auth/auth.guard';

/** Rotas da aplicação (spec 001); cadastro e listagem entram com as tarefas T21 e T22. */
export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    title: $localize`Cadastro — Workspace`,
    canActivate: [visitorGuard],
    loadComponent: () => import('./home/home-page').then((m) => m.HomePage),
  },
  {
    path: 'profile',
    title: $localize`Meu perfil — Cadastro`,
    canActivate: [authGuard],
    loadComponent: () => import('./profile/profile-page').then((m) => m.ProfilePage),
  },
  { path: '**', redirectTo: '' },
];
