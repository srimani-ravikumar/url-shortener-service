import { Location } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api-client';
import { ApiError, describeError } from '../../core/api-error';
import { CredentialsResponse } from '../../core/api.models';
import { AuthService } from '../../core/auth.service';
import { KeyReveal } from '../../shared/key-reveal';

type VerifyState = 'working' | 'done' | 'invalid' | 'taken' | 'failed';

/**
 * The page an emailed link opens. It spends the token in the link, signs the user in with the key the API
 * returns and shows that key once. The token is in the URL fragment so it never reaches a server log; it
 * is also removed from the address bar and history as soon as it has been read.
 */
@Component({
  selector: 'app-verify',
  imports: [KeyReveal, RouterLink],
  templateUrl: './verify.html',
})
export class Verify implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly location = inject(Location);

  protected readonly state = signal<VerifyState>('working');
  protected readonly credentials = signal<CredentialsResponse | null>(null);
  protected readonly failure = signal('');

  async ngOnInit(): Promise<void> {
    const token = new URLSearchParams(this.route.snapshot.fragment ?? '').get('token');
    this.location.replaceState('/verify');

    if (!token) {
      this.state.set('invalid');
      return;
    }

    try {
      const credentials = await firstValueFrom(this.api.verify(token));
      this.auth.acceptCredentials(credentials);
      this.credentials.set(credentials);
      this.state.set('done');
    } catch (error) {
      if (error instanceof ApiError && error.status === 400) {
        this.state.set('invalid');
      } else if (error instanceof ApiError && error.status === 409) {
        this.state.set('taken');
      } else {
        this.failure.set(describeError(error));
        this.state.set('failed');
      }
    }
  }

  /** Drops the key from the page (it is not shown again) and goes to the user's links. */
  protected continue(): void {
    this.credentials.set(null);
    void this.router.navigateByUrl('/links');
  }
}
