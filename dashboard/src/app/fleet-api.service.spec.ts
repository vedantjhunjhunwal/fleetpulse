import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { API_BASE, FleetApiService } from './fleet-api.service';

describe('FleetApiService', () => {
  let api: FleetApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: API_BASE, useValue: '/api' }],
    });
    api = TestBed.inject(FleetApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  // REQ: FR-13
  it('loads fleet KPIs and vehicles', () => {
    api.kpis().subscribe((k) => expect(k.trucks).toBe(24));
    api.vehicles().subscribe((v) => expect(v.length).toBe(1));
    http.expectOne('/api/fleet/kpis').flush({ trucks: 24 });
    http.expectOne('/api/vehicles').flush([{ vehicle: 'trk01' }]);
  });

  // REQ: FR-14
  it('encodes the truck id in the detail URL', () => {
    api.vehicle('trk 01/x').subscribe();
    const req = http.expectOne('/api/vehicles/trk%2001%2Fx');
    expect(req.request.method).toBe('GET');
    req.flush({});
  });

  // REQ: FR-15
  it('loads the maintenance queue', () => {
    api.maintenance().subscribe((q) => expect(q[0].vehicle).toBe('trk22'));
    http.expectOne('/api/maintenance').flush([{ vehicle: 'trk22' }]);
  });
});
