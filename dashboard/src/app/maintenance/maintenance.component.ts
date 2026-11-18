import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BandBadgeComponent } from '../band-badge/band-badge.component';
import { FleetApiService } from '../fleet-api.service';
import { MaintenanceItem } from '../models';
import { formatRisk } from '../risk';

@Component({
  selector: 'fp-maintenance',
  standalone: true,
  imports: [RouterLink, BandBadgeComponent],
  template: `
    <h1>Maintenance queue</h1>
    <p class="meta">Trucks with an active major fault first, then by predicted 7-day fault risk.</p>
    @if (error()) {
      <p class="error" role="alert">{{ error() }}</p>
    }
    @if (items().length === 0 && !error()) {
      <p class="meta">No truck needs workshop time right now.</p>
    }
    <ol class="queue">
      @for (m of items(); track m.vehicle) {
        <li class="card" [class]="'card ' + m.band">
          <div class="head">
            <a [routerLink]="['/vehicles', m.vehicle]">{{ m.vehicle }}</a>
            <fp-band-badge [band]="m.band" />
            <span class="meta">{{ m.system }} · risk {{ risk(m.risk) }}</span>
          </div>
          <p class="action">{{ m.action }}</p>
          @for (f of m.activeFaults; track f) {
            <p class="fault">{{ f }}</p>
          }
        </li>
      }
    </ol>
  `,
})
export class MaintenanceComponent implements OnInit {
  private readonly api = inject(FleetApiService);
  readonly items = signal<MaintenanceItem[]>([]);
  readonly error = signal<string | null>(null);
  readonly risk = formatRisk;

  ngOnInit(): void {
    this.api.maintenance().subscribe({
      next: (q) => this.items.set(q),
      error: () => this.error.set('Could not reach the FleetPulse API.'),
    });
  }
}
