import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api-client';
import { ApiError, describeError } from '../../core/api-error';
import { AnalyticsResponse } from '../../core/api.models';
import { shortUrlFor } from '../../core/short-links';
import { CopyButton } from '../../shared/copy-button';

type DetailState = 'loading' | 'loaded' | 'missing' | 'failed';

/** Click statistics of one link. */
@Component({
  selector: 'app-link-detail',
  imports: [DatePipe, RouterLink, CopyButton],
  templateUrl: './link-detail.html',
})
export class LinkDetail implements OnInit {
  private readonly api = inject(ApiClient);

  protected readonly shortCode = inject(ActivatedRoute).snapshot.paramMap.get('code') ?? '';
  protected readonly shortUrl = shortUrlFor(this.shortCode);
  protected readonly state = signal<DetailState>('loading');
  protected readonly stats = signal<AnalyticsResponse | null>(null);
  protected readonly failure = signal('');

  ngOnInit(): Promise<void> {
    return this.load();
  }

  protected async load(): Promise<void> {
    this.state.set('loading');
    try {
      this.stats.set(await firstValueFrom(this.api.getAnalytics(this.shortCode)));
      this.state.set('loaded');
    } catch (error) {
      // The API answers 404 for an unknown link and 403 for someone else's; to the user both mean "not yours to see".
      if (error instanceof ApiError && (error.status === 404 || error.status === 403)) {
        this.state.set('missing');
      } else {
        this.failure.set(describeError(error));
        this.state.set('failed');
      }
    }
  }
}
