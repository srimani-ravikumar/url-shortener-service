import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api-client';
import { UserUrlItem, ShortUrlResponse } from '../../core/api.models';
import { describeError } from '../../core/api-error';
import { AuthService } from '../../core/auth.service';
import { isExpired, shortCodeOf, shortUrlFor } from '../../core/short-links';
import { CopyButton } from '../../shared/copy-button';
import { ShortenForm } from './shorten-form';

const PAGE_SIZE = 20;

/** The signed-in user's home: create a link and see all their links, newest first. */
@Component({
  selector: 'app-links',
  imports: [DatePipe, RouterLink, CopyButton, ShortenForm],
  templateUrl: './links.html',
  styleUrl: './links.scss',
})
export class Links implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);

  protected readonly items = signal<readonly UserUrlItem[]>([]);
  protected readonly nextPageToken = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly loaded = signal(false);
  protected readonly loadError = signal<string | null>(null);

  protected readonly shortUrlFor = shortUrlFor;
  protected readonly isExpired = isExpired;

  ngOnInit(): Promise<void> {
    return this.loadPage(null);
  }

  protected loadMore(): Promise<void> {
    return this.loadPage(this.nextPageToken());
  }

  /** Shows a link that was just created at the top of the list without asking the API again. */
  protected addCreated(link: ShortUrlResponse): void {
    const item: UserUrlItem = {
      short_code: shortCodeOf(link.short_url),
      long_url: link.long_url,
      created_at: link.created_at,
      expires_at: link.expires_at,
    };
    this.items.update((items) => [item, ...items]);
  }

  private async loadPage(pageToken: string | null): Promise<void> {
    const userId = this.auth.account()?.user_id;
    if (!userId) {
      return;
    }

    this.loading.set(true);
    this.loadError.set(null);
    try {
      const page = await firstValueFrom(this.api.listLinks(userId, PAGE_SIZE, pageToken ?? undefined));
      this.items.update((items) => (pageToken ? [...items, ...page.items] : page.items));
      this.nextPageToken.set(page.next_page_token);
      this.loaded.set(true);
    } catch (error) {
      this.loadError.set(describeError(error));
    } finally {
      this.loading.set(false);
    }
  }
}
