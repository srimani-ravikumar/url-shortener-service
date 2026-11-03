import { Component, inject, output, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api-client';
import { ApiError, describeError } from '../../core/api-error';
import { ShortUrlResponse } from '../../core/api.models';
import { ToastService } from '../../core/toast.service';
import { CopyButton } from '../../shared/copy-button';

/** The "shorten a link" form. Emits the link it created so the list can show it at once. */
@Component({
  selector: 'app-shorten-form',
  imports: [ReactiveFormsModule, CopyButton],
  templateUrl: './shorten-form.html',
})
export class ShortenForm {
  private readonly api = inject(ApiClient);
  private readonly toasts = inject(ToastService);

  readonly created = output<ShortUrlResponse>();

  protected readonly form = new FormGroup({
    longUrl: new FormControl('', { nonNullable: true }),
    customAlias: new FormControl('', { nonNullable: true }),
    expiresAt: new FormControl('', { nonNullable: true }),
  });
  protected readonly errors = signal<Readonly<Record<string, string>>>({});
  protected readonly busy = signal(false);
  protected readonly result = signal<ShortUrlResponse | null>(null);

  protected async submit(): Promise<void> {
    const { longUrl, customAlias, expiresAt } = this.form.getRawValue();

    // The browser's date-time field gives local time without an offset; the API wants an absolute instant.
    const expiry = expiresAt ? new Date(expiresAt) : null;
    if (expiry && Number.isNaN(expiry.getTime())) {
      this.errors.set({ expires_at: 'Enter a valid date and time.' });
      return;
    }

    this.busy.set(true);
    this.errors.set({});
    this.result.set(null);
    try {
      const link = await firstValueFrom(
        this.api.shorten({
          long_url: longUrl.trim(),
          ...(customAlias.trim() && { custom_alias: customAlias.trim() }),
          ...(expiry && { expires_at: expiry.toISOString() }),
        }),
      );
      this.result.set(link);
      this.created.emit(link);
      this.form.reset();
    } catch (error) {
      this.showError(error);
    } finally {
      this.busy.set(false);
    }
  }

  // Field problems go next to their field; everything else (limits, outages) goes to a toast.
  private showError(error: unknown): void {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      const messages: Record<string, string> = {};
      for (const field of Object.keys(error.fieldErrors)) {
        messages[field] = error.fieldError(field) ?? '';
      }

      this.errors.set(messages);
    } else if (error instanceof ApiError && error.status === 409) {
      this.errors.set({ custom_alias: error.userMessage });
    } else {
      this.toasts.error(describeError(error));
    }
  }
}
