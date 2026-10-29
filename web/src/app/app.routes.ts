import { Routes } from '@angular/router';
import { Home } from './pages/home';

/** Page routes. Pages that need a signed-in user are guarded with `authGuard` (added with the pages themselves). */
export const routes: Routes = [
  { path: '', component: Home, title: 'trysmartly.co - URL shortener' },
  { path: '**', redirectTo: '' },
];