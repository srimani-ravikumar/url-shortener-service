import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { buttonWithText, find, provideTestApp, settle, textOf } from '../../../testing/helpers';
import { LinkDetail } from './link-detail';

describe('LinkDetail', () => {
  let http: HttpTestingController;

  function render() {
    TestBed.configureTestingModule({
      providers: [
        ...provideTestApp(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ code: 'abc1234' }) } } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(LinkDetail);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => http.verify());

  it('shows the totals of the link', async () => {
    const fixture = render();

    http.expectOne('/api/v1/analytics/abc1234').flush({
      short_code: 'abc1234',
      total_clicks: 42,
      created_at: '2026-10-29T10:00:00Z',
      last_accessed: '2026-10-29T11:00:00Z',
    });
    await settle(fixture);

    expect(find(fixture, '[data-testid=total-clicks]').textContent).toBe('42');
    expect(textOf(fixture)).toContain('/abc1234');
    expect(textOf(fixture)).not.toContain('Never');
  });

  it('says "Never" for a link nobody has followed', async () => {
    const fixture = render();

    http.expectOne('/api/v1/analytics/abc1234').flush({
      short_code: 'abc1234',
      total_clicks: 0,
      created_at: '2026-10-29T10:00:00Z',
      last_accessed: null,
    });
    await settle(fixture);

    expect(textOf(fixture)).toContain('Never');
  });

  it.each([404, 403])('explains a %i as "not in your account"', async (status) => {
    const fixture = render();

    http.expectOne('/api/v1/analytics/abc1234').flush({ title: 'x' }, { status, statusText: 'Error' });
    await settle(fixture);

    expect(textOf(fixture)).toContain('could not find a link with this code in your account');
  });

  it('shows other failures and can try again', async () => {
    const fixture = render();
    http.expectOne('/api/v1/analytics/abc1234').flush({ title: 'Down', detail: 'Storage is unavailable.' }, { status: 503, statusText: 'Unavailable' });
    await settle(fixture);
    expect(textOf(fixture)).toContain('Storage is unavailable.');

    buttonWithText(fixture, 'Try again').click();
    http.expectOne('/api/v1/analytics/abc1234').flush({ short_code: 'abc1234', total_clicks: 7, created_at: '2026-10-29T10:00:00Z', last_accessed: null });
    await settle(fixture);

    expect(find(fixture, '[data-testid=total-clicks]').textContent).toBe('7');
  });

  it('refreshes the numbers on request', async () => {
    const fixture = render();
    http.expectOne('/api/v1/analytics/abc1234').flush({ short_code: 'abc1234', total_clicks: 1, created_at: '2026-10-29T10:00:00Z', last_accessed: null });
    await settle(fixture);

    buttonWithText(fixture, 'Refresh').click();
    http.expectOne('/api/v1/analytics/abc1234').flush({ short_code: 'abc1234', total_clicks: 2, created_at: '2026-10-29T10:00:00Z', last_accessed: null });
    await settle(fixture);

    expect(find(fixture, '[data-testid=total-clicks]').textContent).toBe('2');
  });
});
