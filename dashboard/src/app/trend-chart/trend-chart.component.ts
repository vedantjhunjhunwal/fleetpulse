import { DecimalPipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';

export interface TrendPoint {
  date: string;
  value: number | null;
}

export interface ChartGeometry {
  path: string;
  min: number;
  max: number;
  xs: number[];
}

/**
 * Maps values to an SVG polyline path inside a width x height box.
 * Missing values break the line instead of being drawn as zero.
 */
export function buildPath(values: (number | null)[], width: number, height: number, pad = 8): ChartGeometry {
  const present = values.filter((v): v is number => v !== null);
  const xs = values.map((_, i) => (values.length < 2 ? width / 2 : pad + (i * (width - 2 * pad)) / (values.length - 1)));
  if (present.length === 0) return { path: '', min: 0, max: 0, xs };
  let min = Math.min(...present);
  let max = Math.max(...present);
  if (max === min) {
    min -= 1;
    max += 1;
  }
  const y = (v: number) => pad + (1 - (v - min) / (max - min)) * (height - 2 * pad);
  let path = '';
  let pen = false;
  values.forEach((v, i) => {
    if (v === null) {
      pen = false;
      return;
    }
    path += `${pen ? 'L' : 'M'}${xs[i].toFixed(1)},${y(v).toFixed(1)} `;
    pen = true;
  });
  return { path: path.trim(), min, max, xs };
}

@Component({
  selector: 'fp-trend-chart',
  standalone: true,
  template: `
    <figure class="chart">
      <figcaption>{{ label() }} <span class="unit">{{ unit() }}</span></figcaption>
      <svg [attr.viewBox]="'0 0 ' + width + ' ' + height" preserveAspectRatio="none" role="img" [attr.aria-label]="label()">
        @for (x of faultXs(); track $index) {
          <line class="fault" [attr.x1]="x" [attr.x2]="x" y1="0" [attr.y2]="height" />
        }
        <path class="line" [attr.d]="geometry().path" />
      </svg>
      <div class="scale">
        <span>{{ firstDate() }}</span>
        <span>{{ geometry().min | number: '1.0-1' }} – {{ geometry().max | number: '1.0-1' }} {{ unit() }}</span>
        <span>{{ lastDate() }}</span>
      </div>
    </figure>
  `,
  styles: `
    .chart { margin: 0; }
    figcaption { font-weight: 600; margin-bottom: 4px; }
    .unit { color: var(--muted); font-weight: 400; }
    svg { width: 100%; height: 150px; background: var(--panel-2); border-radius: 6px; }
    .line { fill: none; stroke: var(--accent); stroke-width: 2; vector-effect: non-scaling-stroke; }
    .fault { stroke: var(--danger); stroke-width: 2; stroke-dasharray: 4 3; vector-effect: non-scaling-stroke; }
    .scale { display: flex; justify-content: space-between; color: var(--muted); font-size: 12px; margin-top: 2px; }
  `,
  imports: [DecimalPipe],
})
export class TrendChartComponent {
  readonly label = input.required<string>();
  readonly unit = input('');
  readonly points = input.required<TrendPoint[]>();
  readonly faultDates = input<string[]>([]);
  readonly width = 600;
  readonly height = 150;

  readonly firstDate = computed(() => this.points().at(0)?.date ?? '');
  readonly lastDate = computed(() => this.points().at(-1)?.date ?? '');
  readonly geometry = computed(() => buildPath(this.points().map((p) => p.value), this.width, this.height));
  readonly faultXs = computed(() => {
    const xs = this.geometry().xs;
    return this.points()
      .map((p, i) => (this.faultDates().includes(p.date) ? xs[i] : null))
      .filter((x): x is number => x !== null);
  });
}
