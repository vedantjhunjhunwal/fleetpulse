import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { API_BASE } from '../fleet-api.service';
import { VehicleDetailComponent } from './vehicle-detail.component';

describe('VehicleDetailComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [VehicleDetailComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: API_BASE, useValue: '/api' }],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  const day = (date: string, coolant: number, major = 0) => ({
    vehicle: 'trk19', date, distanceKm: 280, fuelL: 112, kmPerL: 2.5, coolantCruiseC: coolant, coolantHighLoadC: coolant,
    fuelPressureCruiseKpa: 487, fuelPressureP05Kpa: 472, dtcEvents: major, majorDtcEvents: major,
  });

  // REQ: FR-14
  it('plots the trends, marks the fault day and lists trouble codes', () => {
    const fixture = TestBed.createComponent(VehicleDetailComponent);
    fixture.componentRef.setInput('id', 'trk19');
    fixture.detectChanges();
    http.expectOne('/api/vehicles/trk19').flush({
      summary: { vehicle: 'trk19', days: 3, lastDay: '2026-09-22', totalKm: 840, kmPerL: 2.5, activeDtcs: 0, risk: 0.002, riskBand: 'normal' },
      days: [day('2026-09-17', 99), day('2026-09-19', 106, 400), day('2026-09-22', 88)],
      dtcs: [{
        vehicle: 'trk19', spn: 110, fmi: 0, spnName: 'Engine coolant temperature', fmiText: 'above normal - most severe',
        system: 'Cooling', major: true, firstSeen: '2026-09-19T08:11:00Z', lastSeen: '2026-09-19T10:00:00Z',
        clearedAt: '2026-09-22T06:00:00Z', occurrenceCount: 30, active: false,
      }],
    });
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('h1')!.textContent).toContain('trk19');
    expect(el.querySelectorAll('fp-trend-chart').length).toBe(3);
    expect(el.querySelectorAll('line.fault').length).toBe(3);
    const row = el.querySelector('tbody tr')!;
    expect(row.textContent).toContain('SPN 110 FMI 0');
    expect(row.textContent).toContain('Cleared 2026-09-22');
    expect(row.classList).toContain('major');
  });

  // REQ: FR-14, FR-16
  it('explains an unknown truck', () => {
    const fixture = TestBed.createComponent(VehicleDetailComponent);
    fixture.componentRef.setInput('id', 'trk99');
    fixture.detectChanges();
    http.expectOne('/api/vehicles/trk99').flush({ title: 'Not Found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toContain('trk99 is not in the fleet');
  });
});
