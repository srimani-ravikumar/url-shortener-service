import { Component, computed, inject, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api-client';
import { describeError } from '../../core/api-error';
import { CredentialsResponse } from '../../core/api.models';
import { AuthService } from '../../core/auth.service';
import { ToastService } from '../../core/toast.service';
import { KeyReveal } from '../../shared/key-reveal';

/** The user's account: replace the API key, or delete the account and every link. */
@Component({
  selector: 'app-account',
  imports: [ReactiveFormsModule, KeyReveal],
  templateUrl: './account.html',
})
export class Account {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);

  protected readonly account = this.auth.account;
  protected readonly confirmingRotate = signal(false);
  protected readonly newCredentials = signal<CredentialsResponse | null>(null);
  protected readonly busy = signal(false);

  // The email must be typed out in full before the delete button works, so it cannot be hit by habit.
  protected readonly confirmation = new FormControl('', { nonNullable: true });
  private readonly typed = signal('');
  protected readonly canDelete = computed(() => {
    const email = this.account()?.email;
    return !!email && this.typed().trim().toLowerCase() === email;
  });

  constructor() {
    this.confirmation.valueChanges.subscribe((value) => this.typed.set(value));
  }

  protected async rotate(): Promise<void> {
    this.busy.set(true);
    try {
      const credentials = await firstValueFrom(this.api.rotateKey());
      // The old key is already revoked, so the session must switch to the new one at once.
      this.auth.acceptCredentials(credentials);
      this.newCredentials.set(credentials);
      this.confirmingRotate.set(false);
    } catch (error) {
      this.toasts.error(describeError(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected async deleteAccount(): Promise<void> {
    if (!this.canDelete()) {
      return;
    }

    this.busy.set(true);
    try {
      await firstValueFrom(this.api.deleteAccount());
      this.auth.signOut();
      await this.router.navigateByUrl('/');
      this.toasts.success('Your account and all its links have been deleted.');
    } catch (error) {
      // A failure part-way (for example the cache being down) can simply be tried again.
      this.toasts.error(describeError(error));
    } finally {
      this.busy.set(false);
    }
  }
}
