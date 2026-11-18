import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { API_BASE } from '../fleet-api.service';
import { FleetOverviewComponent } from './fleet-overview.component';

describe('FleetOverviewComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FleetOverviewComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: API_BASE, useValue: '/api' }],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  // REQ: FR-13
  it('shows KPIs and lists trucks with the most urgent first', () => {
    const fixture = TestBed.createComponent(FleetOverviewComponent);
    fixture.detectChanges();
    http.expectOne('/api/fleet/kpis').flush({
      trucks: 3, operatingDays: 150, totalKm: 41234.5, fleetKmPerL: 2.517, activeDtcs: 2, activeMajorDtcs: 2, highRisk: 1,
      riskModel: 'm', lastIngest: null,
    });
    http.expectOne('/api/vehicles').flush([
      { vehicle: 'trk01', days: 50, lastDay: '2026-10-01', totalKm: 13000, kmPerL: 2.5, activeDtcs: 0, risk: 0.01, riskBand: 'normal' },
      { vehicle: 'trk02', days: 50, lastDay: '2026-10-01', totalKm: 14000, kmPerL: 2.4, activeDtcs: 0, risk: 0.8, riskBand: 'high' },
      { vehicle: 'trk03', days: 49, lastDay: '2026-09-29', totalKm: 14234, kmPerL: 2.6, activeDtcs: 2, risk: null, riskBand: 'down' },
    ]);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    const kpis = Array.from(el.querySelectorAll('.kpi .v')).map((e) => e.textContent!.trim());
    expect(kpis).toEqual(['3', '41,235', '2.52', '2', '1']);
    const trucks = Array.from(el.querySelectorAll('tbody tr td:first-child')).map((e) => e.textContent!.trim());
    expect(trucks).toEqual(['trk03', 'trk02', 'trk01']);
    expect(el.querySelector('tbody tr')!.textContent).toContain('Fault active');
    expect(el.querySelectorAll('tbody tr')[1].textContent).toContain('80%');
  });

  // REQ: FR-13
  it('shows an error when the API is unreachable', () => {
    const fixture = TestBed.createComponent(FleetOverviewComponent);
    fixture.detectChanges();
    http.expectOne('/api/fleet/kpis').error(new ProgressEvent('error'));
    http.match('/api/vehicles');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toContain('Could not reach');
  });
});
