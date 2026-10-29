import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiError, describeError } from '../../core/api-error';
import { AuthService } from '../../core/auth.service';

/** The start page: sign in by pasting an API key. */
@Component({
  selector: 'app-sign-in',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './sign-in.html',
})
export class SignIn {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly form = new FormGroup({
    apiKey: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected async submit(): Promise<void> {
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.error.set('Enter your API key.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      await this.auth.signIn(this.form.controls.apiKey.value);
      await this.router.navigateByUrl('/links');
    } catch (error) {
      this.error.set(
        error instanceof ApiError && error.status === 401
          ? 'That key is not valid. Check it, or get a new one.'
          : describeError(error),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
