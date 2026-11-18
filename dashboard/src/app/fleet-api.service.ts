import { HttpClient } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { FleetKpis, MaintenanceItem, VehicleDetail, VehicleRow } from './models';

/** Base URL of the FleetPulse API; override with { provide: API_BASE, useValue: '...' }. */
export const API_BASE = new InjectionToken<string>('API_BASE', {
  providedIn: 'root',
  factory: () => 'http://localhost:5080/api',
});

@Injectable({ providedIn: 'root' })
export class FleetApiService {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE);

  kpis(): Observable<FleetKpis> {
    return this.http.get<FleetKpis>(`${this.base}/fleet/kpis`);
  }

  vehicles(): Observable<VehicleRow[]> {
    return this.http.get<VehicleRow[]>(`${this.base}/vehicles`);
  }

  vehicle(id: string): Observable<VehicleDetail> {
    return this.http.get<VehicleDetail>(`${this.base}/vehicles/${encodeURIComponent(id)}`);
  }

  maintenance(): Observable<MaintenanceItem[]> {
    return this.http.get<MaintenanceItem[]>(`${this.base}/maintenance`);
  }
}
