import { Component, inject, input, signal } from '@angular/core';
import { ToastService } from '../core/toast.service';

/** A button that copies `text` to the clipboard and briefly says so. */
@Component({
  selector: 'app-copy-button',
  template: `
    <button type="button" class="btn" (click)="copy()">
      {{ copied() ? 'Copied' : label() }}
    </button>
  `,
})
export class CopyButton {
  private readonly toasts = inject(ToastService);

  readonly text = input.required<string>();
  readonly label = input('Copy');

  protected readonly copied = signal(false);

  protected async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.text());
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 2000);
    } catch {
      // The clipboard needs a secure page and the user's permission; either can be missing.
      this.toasts.error('Could not copy. Select the text and copy it by hand.');
    }
  }
}
