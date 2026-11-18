/** Shapes returned by the FleetPulse API (/api). */

export type RiskBand = 'down' | 'high' | 'watch' | 'normal' | 'unscored';

export interface LastIngest {
  lines: number;
  frames: number;
  parseErrors: number;
  bamMessages: number;
}

export interface FleetKpis {
  trucks: number;
  operatingDays: number;
  totalKm: number;
  fleetKmPerL: number | null;
  activeDtcs: number;
  activeMajorDtcs: number;
  highRisk: number;
  riskModel: string | null;
  lastIngest: LastIngest | null;
}

export interface VehicleRow {
  vehicle: string;
  days: number;
  lastDay: string | null;
  totalKm: number;
  kmPerL: number | null;
  activeDtcs: number;
  risk: number | null;
  riskBand: RiskBand;
}

export interface DailySummary {
  vehicle: string;
  date: string;
  distanceKm: number;
  fuelL: number;
  kmPerL: number | null;
  coolantCruiseC: number | null;
  coolantHighLoadC: number | null;
  fuelPressureCruiseKpa: number | null;
  fuelPressureP05Kpa: number | null;
  dtcEvents: number;
  majorDtcEvents: number;
}

export interface Dtc {
  vehicle: string;
  spn: number;
  fmi: number;
  spnName: string;
  fmiText: string;
  system: string;
  major: boolean;
  firstSeen: string;
  lastSeen: string;
  clearedAt: string | null;
  occurrenceCount: number;
  active: boolean;
}

export interface VehicleDetail {
  summary: VehicleRow;
  days: DailySummary[];
  dtcs: Dtc[];
}

export interface MaintenanceItem {
  vehicle: string;
  band: RiskBand;
  risk: number | null;
  system: string;
  action: string;
  activeFaults: string[];
}
