import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { LoadingService } from './core/loading.service';
import { Header } from './layout/header';
import { Toasts } from './layout/toasts';

/** The page frame: header, a progress bar while the API is being called, the current page and the toasts. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, Header, Toasts],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly loading = inject(LoadingService);
}
