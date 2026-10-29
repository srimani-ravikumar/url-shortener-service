import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { EnvironmentProviders, Provider } from '@angular/core';
import { ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { KEY_STORAGE } from '../app/core/auth.service';
import { errorInterceptor } from '../app/core/interceptors';
import { FakeStorage } from './fake-storage';

/** Providers that give a component under test a fake API (HttpTestingController), a router and an empty key store. */
export function provideTestApp(storage: Storage = new FakeStorage()): (Provider | EnvironmentProviders)[] {
  return [
    provideRouter([]),
    // The error interceptor is included so failures arrive as ApiError, as in the real app.
    provideHttpClient(withInterceptors([errorInterceptor])),
    provideHttpClientTesting(),
    { provide: KEY_STORAGE, useValue: storage },
  ];
}

/** Lets pending promises finish (an awaited HTTP call, for example), then redraws the component. */
export async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

/** Types into an input the way a user does, so Angular forms notice. */
export function typeInto(element: HTMLInputElement, value: string): void {
  element.value = value;
  element.dispatchEvent(new Event('input'));
}

/** The first element matching a selector; fails the test with a clear message if there is none. */
export function find<T extends HTMLElement = HTMLElement>(fixture: ComponentFixture<unknown>, selector: string): T {
  const element = (fixture.nativeElement as HTMLElement).querySelector<T>(selector);
  if (!element) {
    throw new Error(`Nothing matches "${selector}" in:\n${(fixture.nativeElement as HTMLElement).innerHTML}`);
  }

  return element;
}

/** The text content of the whole component, with whitespace collapsed, for simple "shows this" checks. */
export function textOf(fixture: ComponentFixture<unknown>): string {
  return ((fixture.nativeElement as HTMLElement).textContent ?? '').replace(/\s+/g, ' ').trim();
}

/** The button whose text contains `text`; fails the test with a clear message if there is none. */
export function buttonWithText(fixture: ComponentFixture<unknown>, text: string): HTMLButtonElement {
  const buttons = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')];
  const button = buttons.find((candidate) => candidate.textContent?.includes(text));
  if (!button) {
    throw new Error(`No button says "${text}". Buttons: ${buttons.map((b) => b.textContent?.trim()).join(' | ')}`);
  }

  return button;
}
