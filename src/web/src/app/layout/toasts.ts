import { Component, inject } from '@angular/core';
import { ToastService } from '../core/toast.service';

/** Shows the messages from {@link ToastService} in the corner; errors are announced to screen readers at once. */
@Component({
  selector: 'app-toasts',
  templateUrl: './toasts.html',
  styleUrl: './toasts.scss',
})
export class Toasts {
  protected readonly toasts = inject(ToastService);
}
