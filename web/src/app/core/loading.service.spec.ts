import { TestBed } from '@angular/core/testing';
import { LoadingService } from './loading.service';

describe('LoadingService', () => {
  it('is busy while any call is running', () => {
    const loading = TestBed.inject(LoadingService);
    expect(loading.busy()).toBe(false);

    loading.begin();
    loading.begin();
    expect(loading.busy()).toBe(true);

    loading.end();
    expect(loading.busy()).toBe(true);

    loading.end();
    expect(loading.busy()).toBe(false);
  });

  it('never counts below zero', () => {
    const loading = TestBed.inject(LoadingService);

    loading.end();
    loading.begin();

    expect(loading.busy()).toBe(true);
  });
});
