import { TestBed } from '@angular/core/testing';
import { ToastService } from '../core/toast.service';
import { Toasts } from './toasts';

describe('Toasts', () => {
  function render() {
    const fixture = TestBed.createComponent(Toasts);
    fixture.detectChanges();
    return fixture;
  }

  it('shows each message, announcing errors as alerts and the rest as status updates', () => {
    const service = TestBed.inject(ToastService);
    service.error('It failed');
    service.success('It worked');

    const element: HTMLElement = render().nativeElement;
    const toasts = [...element.querySelectorAll('.toast')];

    expect(toasts.map((toast) => toast.getAttribute('role'))).toEqual(['alert', 'status']);
    expect(toasts[0].textContent).toContain('It failed');
    expect(toasts[1].textContent).toContain('It worked');
  });

  it('removes a message when its dismiss button is pressed', () => {
    const service = TestBed.inject(ToastService);
    service.show('Hello');
    const fixture = render();

    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.close')?.click();
    fixture.detectChanges();

    expect(service.toasts()).toHaveLength(0);
    expect((fixture.nativeElement as HTMLElement).querySelector('.toast')).toBeNull();
  });
});
