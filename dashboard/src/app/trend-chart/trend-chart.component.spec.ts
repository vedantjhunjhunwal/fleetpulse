import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TrendChartComponent, buildPath } from './trend-chart.component';

describe('TrendChartComponent', () => {
  // REQ: FR-14
  it('scales values into the box and breaks the line on missing values', () => {
    const g = buildPath([10, 20, null, 30], 100, 50, 0);
    expect(g.min).toBe(10);
    expect(g.max).toBe(30);
    expect(g.path).toBe('M0.0,50.0 L33.3,25.0 M100.0,0.0');
  });

  // REQ: FR-14
  it('handles flat and empty series', () => {
    expect(buildPath([5, 5], 100, 50, 0).path).toBe('M0.0,25.0 L100.0,25.0');
    expect(buildPath([null, null], 100, 50).path).toBe('');
  });

  // REQ: FR-14
  it('draws a marker on each fault day', async () => {
    await TestBed.configureTestingModule({ imports: [TrendChartComponent] }).compileComponents();
    const fixture: ComponentFixture<TrendChartComponent> = TestBed.createComponent(TrendChartComponent);
    fixture.componentRef.setInput('label', 'Coolant');
    fixture.componentRef.setInput('unit', '°C');
    fixture.componentRef.setInput('points', [
      { date: '2026-09-01', value: 88 },
      { date: '2026-09-02', value: 104 },
      { date: '2026-09-03', value: 89 },
    ]);
    fixture.componentRef.setInput('faultDates', ['2026-09-02']);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelectorAll('line.fault').length).toBe(1);
    expect(el.querySelector('path.line')!.getAttribute('d')).toContain('M');
    expect(el.textContent).toContain('2026-09-03');
  });
});
