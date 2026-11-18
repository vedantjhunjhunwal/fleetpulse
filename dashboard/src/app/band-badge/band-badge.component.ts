import { Component, computed, input } from '@angular/core';
import { RiskBand } from '../models';
import { BAND_LABEL } from '../risk';

@Component({
  selector: 'fp-band-badge',
  standalone: true,
  template: `<span class="badge" [class]="'badge ' + band()">{{ label() }}</span>`,
  styles: `
    .badge { display: inline-block; padding: 2px 10px; border-radius: 999px; font-size: 12px; font-weight: 600; white-space: nowrap; }
    .down { background: var(--danger); color: #fff; }
    .high { background: #f59e0b; color: #1f1300; }
    .watch { background: #fde68a; color: #3d2f00; }
    .normal { background: var(--ok-bg); color: var(--ok); }
    .unscored { background: var(--panel-2); color: var(--muted); }
  `,
})
export class BandBadgeComponent {
  readonly band = input.required<RiskBand>();
  readonly label = computed(() => BAND_LABEL[this.band()]);
}
