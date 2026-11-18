import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BandBadgeComponent } from '../band-badge/band-badge.component';
import { FleetApiService } from '../fleet-api.service';
import { VehicleDetail } from '../models';
import { formatRisk } from '../risk';
import { TrendChartComponent, TrendPoint } from '../trend-chart/trend-chart.component';

@Component({
  selector: 'fp-vehicle-detail',
  standalone: true,
  imports: [DatePipe, DecimalPipe, RouterLink, BandBadgeComponent, TrendChartComponent],
  template: `
    <p><a routerLink="/">← Fleet</a></p>
    @if (error()) {
      <p class="error" role="alert">{{ error() }}</p>
    }
    @if (detail(); as d) {
      <h1>{{ d.summary.vehicle }} <fp-band-badge [band]="d.summary.riskBand" /></h1>
      <p class="meta">
        {{ d.summary.days }} operating days · {{ d.summary.totalKm | number: '1.0-0' }} km ·
        {{ d.summary.kmPerL | number: '1.2-2' }} km/L · 7-day fault risk {{ risk(d.summary.risk) }}
      </p>
      <section class="charts">
        <fp-trend-chart label="Coolant temperature under high load" unit="°C" [points]="coolant()" [faultDates]="faultDates()" />
        <fp-trend-chart label="Fuel delivery pressure (5th percentile)" unit="kPa" [points]="fuelPressure()" [faultDates]="faultDates()" />
        <fp-trend-chart label="Fuel economy" unit="km/L" [points]="economy()" [faultDates]="faultDates()" />
      </section>
      <h2>Diagnostic trouble codes</h2>
      @if (d.dtcs.length === 0) {
        <p class="meta">No trouble codes recorded.</p>
      } @else {
        <table class="grid">
          <thead><tr><th>Code</th><th>Component</th><th>Failure mode</th><th>First seen</th><th>Status</th></tr></thead>
          <tbody>
            @for (c of d.dtcs; track c.spn + '-' + c.fmi) {
              <tr [class.major]="c.major">
                <td>SPN {{ c.spn }} FMI {{ c.fmi }}</td>
                <td>{{ c.spnName }}</td>
                <td>{{ c.fmiText }}</td>
                <td>{{ c.firstSeen | date: 'yyyy-MM-dd HH:mm' : 'UTC' }}</td>
                <td>{{ c.active ? 'Active' : 'Cleared ' + (c.clearedAt | date: 'yyyy-MM-dd' : 'UTC') }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
    }
  `,
})
export class VehicleDetailComponent implements OnInit {
  /** Route parameter, bound with withComponentInputBinding(). */
  readonly id = input.required<string>();
  private readonly api = inject(FleetApiService);
  readonly detail = signal<VehicleDetail | null>(null);
  readonly error = signal<string | null>(null);
  readonly risk = formatRisk;

  private series(pick: (d: VehicleDetail['days'][number]) => number | null): TrendPoint[] {
    return (this.detail()?.days ?? []).map((d) => ({ date: d.date, value: pick(d) }));
  }

  readonly coolant = computed(() => this.series((d) => d.coolantHighLoadC));
  readonly fuelPressure = computed(() => this.series((d) => d.fuelPressureP05Kpa));
  readonly economy = computed(() => this.series((d) => d.kmPerL));
  readonly faultDates = computed(() => (this.detail()?.days ?? []).filter((d) => d.majorDtcEvents > 0).map((d) => d.date));

  ngOnInit(): void {
    this.api.vehicle(this.id()).subscribe({
      next: (d) => this.detail.set(d),
      error: (e) => this.error.set(e?.status === 404 ? `Truck ${this.id()} is not in the fleet.` : 'Could not reach the FleetPulse API.'),
    });
  }
}
