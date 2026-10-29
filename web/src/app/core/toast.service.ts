import { Injectable, signal } from '@angular/core';

export type ToastKind = 'error' | 'success' | 'info';

export interface Toast {
  readonly id: number;
  readonly kind: ToastKind;
  readonly message: string;
}

/** Short messages shown over the page that go away by themselves. */
@Injectable({ providedIn: 'root' })
export class ToastService {
  private nextId = 1;
  private readonly toastsSignal = signal<readonly Toast[]>([]);

  /** The toasts currently shown, oldest first. */
  readonly toasts = this.toastsSignal.asReadonly();

  show(message: string, kind: ToastKind = 'info', durationMs = 6000): void {
    const toast: Toast = { id: this.nextId++, kind, message };
    this.toastsSignal.update((toasts) => [...toasts, toast]);
    setTimeout(() => this.dismiss(toast.id), durationMs);
  }

  error(message: string): void {
    // Errors stay a little longer: they are the ones people need to read.
    this.show(message, 'error', 10000);
  }

  success(message: string): void {
    this.show(message, 'success');
  }

  dismiss(id: number): void {
    this.toastsSignal.update((toasts) => toasts.filter((toast) => toast.id !== id));
  }
}
