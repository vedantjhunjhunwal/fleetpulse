# FleetPulse predictive-maintenance report

- Truck-days scored: 1038 (54 inside the 7 days before a fault)
- Out-of-fold ROC AUC (4 folds grouped by truck): 0.999
- Faults flagged in advance: 9 of 9; median warning 6 days (minimum 5)
- Alert rule: risk at or above the threshold on 2 operating days in a row
- False alert episodes: 1 (0.96 per 1,000 truck-days) on trk07

## Warning time per failed truck

| Truck | Days of warning |
|---|---|
| trk03 | 7 |
| trk06 | 8 |
| trk08 | 8 |
| trk10 | 6 |
| trk14 | 6 |
| trk16 | 9 |
| trk19 | 5 |
| trk21 | 6 |
| trk22 | 6 |

## Current risk (final model, 21 of 24 trucks scored on their latest day)

| Truck | Date | Risk | Driver |
|---|---|---|---|
| trk21 | 2026-10-01 | 0.021 | fuel |
| trk23 | 2026-10-01 | 0.014 | fuel |
| trk10 | 2026-10-01 | 0.008 | cooling |
| trk17 | 2026-10-01 | 0.004 | fuel |
| trk18 | 2026-10-01 | 0.004 | fuel |
| trk20 | 2026-10-01 | 0.003 | fuel |
| trk02 | 2026-10-01 | 0.002 | cooling |
| trk04 | 2026-10-01 | 0.001 | cooling |
| trk11 | 2026-10-01 | 0.001 | fuel |
| trk24 | 2026-10-01 | 0.001 | fuel |
| trk13 | 2026-10-01 | 0.000 | cooling |
| trk15 | 2026-10-01 | 0.000 | fuel |
| trk07 | 2026-10-01 | 0.000 | cooling |
| trk12 | 2026-10-01 | 0.000 | fuel |
| trk19 | 2026-10-01 | 0.000 | fuel |
| trk01 | 2026-10-01 | 0.000 | cooling |
| trk03 | 2026-10-01 | 0.000 | cooling |
| trk05 | 2026-10-01 | 0.000 | cooling |
| trk06 | 2026-10-01 | 0.000 | fuel |
| trk14 | 2026-10-01 | 0.000 | fuel |
| trk09 | 2026-10-01 | 0.000 | fuel |

Alert threshold: 0.0384 (2% single-day false-alarm rate on training days); risk shown is the lower of the last 2 days.
