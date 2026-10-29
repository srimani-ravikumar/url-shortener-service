import { TestBed } from '@angular/core/testing';
import { Component } from '@angular/core';
import { find, settle } from '../../testing/helpers';
import { ToastService } from '../core/toast.service';
import { CopyButton } from './copy-button';
import { KeyReveal } from './key-reveal';

@Component({ imports: [CopyButton], template: '<app-copy-button text="hello" />' })
class CopyHost {}

describe('CopyButton', () => {
  const writeText = vi.fn();

  beforeEach(() => {
    writeText.mockReset();
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
  });

  it('copies the text and says "Copied" for a moment', async () => {
    writeText.mockResolvedValue(undefined);
    const fixture = TestBed.createComponent(CopyHost);
    fixture.detectChanges();

    find(fixture, 'button').click();
    await settle(fixture);

    expect(writeText).toHaveBeenCalledWith('hello');
    expect(find(fixture, 'button').textContent).toContain('Copied');
  });

  it('asks the user to copy by hand when the clipboard is refused', async () => {
    writeText.mockRejectedValue(new Error('denied'));
    const fixture = TestBed.createComponent(CopyHost);
    fixture.detectChanges();

    find(fixture, 'button').click();
    await settle(fixture);

    expect(TestBed.inject(ToastService).toasts()[0].message).toContain('Select the text');
    expect(find(fixture, 'button').textContent).toContain('Copy');
  });
});

describe('KeyReveal', () => {
  it('shows the key with the once-only warning and a copy button', () => {
    const fixture = TestBed.createComponent(KeyReveal);
    fixture.componentRef.setInput('apiKey', 'abc-key');
    fixture.detectChanges();

    expect(find(fixture, '[data-testid=api-key]').textContent).toBe('abc-key');
    expect(find(fixture, '.warning').textContent).toContain('only time the key is shown');
    expect(find(fixture, 'app-copy-button button').textContent).toContain('Copy key');
  });
});
