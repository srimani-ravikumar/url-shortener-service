import { TestBed } from '@angular/core/testing';
import { ToastService } from './toast.service';

describe('ToastService', () => {
  let toasts: ToastService;

  beforeEach(() => {
    vi.useFakeTimers();
    toasts = TestBed.inject(ToastService);
  });

  afterEach(() => vi.useRealTimers());

  it('shows messages in the order they arrive, with their kind', () => {
    toasts.success('Saved');
    toasts.error('Failed');

    expect(toasts.toasts().map((toast) => [toast.kind, toast.message])).toEqual([
      ['success', 'Saved'],
      ['error', 'Failed'],
    ]);
  });

  it('removes a message after its time is up', () => {
    toasts.show('Hello', 'info', 1000);

    vi.advanceTimersByTime(999);
    expect(toasts.toasts()).toHaveLength(1);

    vi.advanceTimersByTime(1);
    expect(toasts.toasts()).toHaveLength(0);
  });

  it('keeps errors longer than other messages', () => {
    toasts.success('Saved');
    toasts.error('Failed');

    vi.advanceTimersByTime(6000);
    expect(toasts.toasts().map((toast) => toast.message)).toEqual(['Failed']);

    vi.advanceTimersByTime(4000);
    expect(toasts.toasts()).toHaveLength(0);
  });

  it('dismisses only the chosen message', () => {
    toasts.show('One');
    toasts.show('Two');
    const [first] = toasts.toasts();

    toasts.dismiss(first.id);

    expect(toasts.toasts().map((toast) => toast.message)).toEqual(['Two']);
  });
});
