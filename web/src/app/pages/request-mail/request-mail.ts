import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api-client';
import { ApiError, describeError } from '../../core/api-error';
import { ToastService } from '../../core/toast.service';

/** Which email the page asks for: a new account's key, or a replacement for a lost one. */
export type MailMode = 'signup' | 'recover';

/**
 * Asks for an email address and has the API mail a link. The same page serves both flows (route data `mode`).
 * Whatever the address, a valid request gets the same "check your inbox" answer, as the API's does.
 */
@Component({
  selector: 'app-request-mail',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './request-mail.html',
})
export class RequestMail {
  private readonly api = inject(ApiClient);
  private readonly toasts = inject(ToastService);

  protected readonly mode: MailMode = inject(ActivatedRoute).snapshot.data['mode'] === 'recover' ? 'recover' : 'signup';
  protected readonly form = new FormGroup({
    email: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });
  protected readonly emailError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly sentTo = signal<string | null>(null);

  protected async submit(): Promise<void> {
    const email = this.form.controls.email.value.trim();
    if (!email) {
      this.emailError.set('Enter your email address.');
      return;
    }

    this.busy.set(true);
    this.emailError.set(null);
    try {
      await firstValueFrom(this.mode === 'signup' ? this.api.signUp(email) : this.api.recover(email));
      this.sentTo.set(email);
    } catch (error) {
      const fieldMessage = error instanceof ApiError ? error.fieldError('email') : null;
      if (fieldMessage) {
        this.emailError.set(fieldMessage);
      } else {
        this.toasts.error(describeError(error));
      }
    } finally {
      this.busy.set(false);
    }
  }
}
