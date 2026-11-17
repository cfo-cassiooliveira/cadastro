import { Routes } from '@angular/router';
import { authGuard, visitorGuard } from './auth/auth.guard';
import { LOG_INVESTIGATOR_ROLE } from './auth/auth.model';
import { logSearchGuard } from './log-search/log-search.guard';

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
  {
    path: 'log-search',
    title: $localize`Pesquisa em logs — Cadastro`,
    canActivate: [logSearchGuard(LOG_INVESTIGATOR_ROLE)],
    loadComponent: () => import('./log-search/log-search-page').then((m) => m.LogSearchPage),
  },
  {
    path: 'forbidden',
    title: $localize`Acesso negado — Cadastro`,
    canActivate: [authGuard],
    loadComponent: () => import('./forbidden/forbidden-page').then((m) => m.ForbiddenPage),
  },
  { path: '**', redirectTo: '' },
];
