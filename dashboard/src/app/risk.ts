import { RiskBand } from './models';

const ORDER: Record<RiskBand, number> = { down: 0, high: 1, watch: 2, normal: 3, unscored: 4 };

export const BAND_LABEL: Record<RiskBand, string> = {
  down: 'Fault active',
  high: 'High risk',
  watch: 'Watch',
  normal: 'Normal',
  unscored: 'Not scored',
};

/** Sort key: trucks needing action first, then by descending risk. */
export function byUrgency<T extends { riskBand: RiskBand; risk: number | null; vehicle: string }>(a: T, b: T): number {
  return ORDER[a.riskBand] - ORDER[b.riskBand] || (b.risk ?? -1) - (a.risk ?? -1) || a.vehicle.localeCompare(b.vehicle);
}

export function formatRisk(risk: number | null | undefined): string {
  return risk === null || risk === undefined ? '–' : `${Math.round(risk * 100)}%`;
}
