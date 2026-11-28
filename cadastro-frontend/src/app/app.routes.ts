import { Routes } from '@angular/router';
import { authGuard, roleGuard, visitorGuard } from './auth/auth.guard';
import { LOG_INVESTIGATOR_ROLE, USER_IMPORTER_ROLE } from './auth/auth.model';
import { logSearchGuard } from './log-search/log-search.guard';

/**
 * Rotas da aplicação, todas carregadas sob demanda. Os guards só evitam abrir uma tela que a pessoa não pode usar: quem
 * autoriza é a API.
 */
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
    path: 'imports',
    canActivate: [roleGuard(USER_IMPORTER_ROLE)],
    children: [
      {
        path: '',
        title: $localize`Importações em lote — Cadastro`,
        loadComponent: () =>
          import('./user-imports/user-import-list-page').then((m) => m.UserImportListPage),
      },
      {
        path: 'new',
        title: $localize`Enviar lista — Cadastro`,
        loadComponent: () =>
          import('./user-imports/user-import-upload-page').then((m) => m.UserImportUploadPage),
      },
      {
        path: ':id',
        title: $localize`Lote de importação — Cadastro`,
        loadComponent: () =>
          import('./user-imports/user-import-detail-page').then((m) => m.UserImportDetailPage),
      },
    ],
  },
  {
    path: 'forbidden',
    title: $localize`Acesso negado — Cadastro`,
    canActivate: [authGuard],
    loadComponent: () => import('./forbidden/forbidden-page').then((m) => m.ForbiddenPage),
  },
  { path: '**', redirectTo: '' },
];
