import { RiskBand } from './models';
import { BAND_LABEL, byUrgency, formatRisk } from './risk';

describe('risk helpers', () => {
  const row = (vehicle: string, riskBand: RiskBand, risk: number | null) => ({ vehicle, riskBand, risk });

  // REQ: FR-12, FR-13
  it('orders trucks by urgency, then risk, then name', () => {
    const rows = [
      row('trk05', 'normal', 0.01),
      row('trk02', 'high', 0.6),
      row('trk09', 'unscored', null),
      row('trk01', 'high', 0.9),
      row('trk07', 'down', null),
      row('trk03', 'normal', 0.01),
      row('trk04', 'watch', 0.3),
    ];
    expect(rows.sort(byUrgency).map((r) => r.vehicle)).toEqual(['trk07', 'trk01', 'trk02', 'trk04', 'trk03', 'trk05', 'trk09']);
  });

  // REQ: FR-13
  it('formats risk as a percentage and labels every band', () => {
    expect(formatRisk(0.456)).toBe('46%');
    expect(formatRisk(null)).toBe('–');
    expect(formatRisk(undefined)).toBe('–');
    expect(Object.keys(BAND_LABEL).length).toBe(5);
  });
});
