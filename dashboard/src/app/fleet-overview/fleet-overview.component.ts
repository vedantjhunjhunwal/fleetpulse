import { DecimalPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { BandBadgeComponent } from '../band-badge/band-badge.component';
import { FleetApiService } from '../fleet-api.service';
import { FleetKpis, VehicleRow } from '../models';
import { byUrgency, formatRisk } from '../risk';

@Component({
  selector: 'fp-fleet-overview',
  standalone: true,
  imports: [DecimalPipe, RouterLink, BandBadgeComponent],
  template: `
    <h1>Fleet overview</h1>
    @if (error()) {
      <p class="error" role="alert">{{ error() }}</p>
    }
    @if (kpis(); as k) {
      <section class="kpis">
        <div class="kpi"><span class="v">{{ k.trucks }}</span><span class="l">Trucks</span></div>
        <div class="kpi"><span class="v">{{ k.totalKm | number: '1.0-0' }}</span><span class="l">km driven</span></div>
        <div class="kpi"><span class="v">{{ k.fleetKmPerL | number: '1.2-2' }}</span><span class="l">Fleet km/L</span></div>
        <div class="kpi" [class.alert]="k.activeMajorDtcs > 0"><span class="v">{{ k.activeMajorDtcs }}</span><span class="l">Active major faults</span></div>
        <div class="kpi" [class.warn]="k.highRisk > 0"><span class="v">{{ k.highRisk }}</span><span class="l">High-risk trucks</span></div>
      </section>
    }
    <table class="grid">
      <thead>
        <tr><th>Truck</th><th>Status</th><th>7-day fault risk</th><th>Days</th><th>km</th><th>km/L</th><th>Active DTCs</th><th>Last day</th></tr>
      </thead>
      <tbody>
        @for (v of vehicles(); track v.vehicle) {
          <tr>
            <td><a [routerLink]="['/vehicles', v.vehicle]">{{ v.vehicle }}</a></td>
            <td><fp-band-badge [band]="v.riskBand" /></td>
            <td class="num">{{ risk(v.risk) }}</td>
            <td class="num">{{ v.days }}</td>
            <td class="num">{{ v.totalKm | number: '1.0-0' }}</td>
            <td class="num">{{ v.kmPerL | number: '1.2-2' }}</td>
            <td class="num">{{ v.activeDtcs }}</td>
            <td>{{ v.lastDay }}</td>
          </tr>
        }
      </tbody>
    </table>
  `,
})
export class FleetOverviewComponent implements OnInit {
  private readonly api = inject(FleetApiService);
  readonly kpis = signal<FleetKpis | null>(null);
  readonly vehicles = signal<VehicleRow[]>([]);
  readonly error = signal<string | null>(null);
  readonly risk = formatRisk;

  ngOnInit(): void {
    forkJoin({ kpis: this.api.kpis(), vehicles: this.api.vehicles() }).subscribe({
      next: ({ kpis, vehicles }) => {
        this.kpis.set(kpis);
        this.vehicles.set([...vehicles].sort(byUrgency));
      },
      error: () => this.error.set('Could not reach the FleetPulse API.'),
    });
  }
}
