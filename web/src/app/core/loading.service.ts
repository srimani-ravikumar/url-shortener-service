import { Injectable, computed, signal } from '@angular/core';

/** Counts the API calls in flight, so the page can show one progress bar however many run at once. */
@Injectable({ providedIn: 'root' })
export class LoadingService {
  private readonly pending = signal(0);

  /** True while at least one call is running. */
  readonly busy = computed(() => this.pending() > 0);

  begin(): void {
    this.pending.update((count) => count + 1);
  }

  end(): void {
    this.pending.update((count) => Math.max(0, count - 1));
  }
}
