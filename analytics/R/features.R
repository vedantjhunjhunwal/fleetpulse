# Feature engineering for FleetPulse predictive maintenance.
#
# Input: the daily feature export from the API (one row per truck per operating
# day). Raw temperatures and pressures depend on weather, route and payload, so
# every feature is made relative twice:
#   1. to the fleet that day (removes heatwaves and seasonal temperature), and
#   2. to the truck's own recent history (removes hilly routes, heavier loads
#      and individual sensor offsets).
# A fault shows up as a sustained drift away from the truck's own baseline.

FEATURES <- c("coolant_dev", "coolant_slope", "fuelp_dev", "fuelp_slope", "econ_dev_pct")
COOLING_FEATURES <- c("coolant_dev", "coolant_slope")
FUEL_FEATURES <- c("fuelp_dev", "fuelp_slope", "econ_dev_pct")

read_daily <- function(path) {
  d <- utils::read.csv(path, na.strings = "NA", stringsAsFactors = FALSE)
  d$date <- as.Date(d$date)
  d[order(d$vehicle, d$date), ]
}

# value minus the fleet median for the same date
fleet_relative <- function(d, col) {
  med <- stats::ave(d[[col]], d$date, FUN = function(x) stats::median(x, na.rm = TRUE))
  d[[col]] - med
}

# OLS slope of y against day number; NA with fewer than 3 points
slope <- function(days, y) {
  ok <- !is.na(y)
  if (sum(ok) < 3) return(NA_real_)
  x <- as.numeric(days[ok])
  y <- y[ok]
  if (stats::var(x) == 0) return(NA_real_)
  sum((x - mean(x)) * (y - mean(y))) / sum((x - mean(x))^2)
}

# For each row: median of `x` over the same truck's days in [date - from, date - to],
# restricted to days after the truck's last repair. NA with fewer than `min_n` days.
# When fewer than `min_n` days exist (start of the data, or just after a repair),
# the window falls back to [date - from, date - to_short] so a repaired truck is
# re-baselined from its post-repair days within about a week.
own_baseline <- function(dates, x, repaired_after, from = 35, to = 10, to_short = 3, min_n = 4) {
  vapply(seq_along(dates), function(i) {
    lo <- max(dates[i] - from, repaired_after[i] + 1)
    for (gap in c(to, to_short)) {
      sel <- dates >= lo & dates <= dates[i] - gap & !is.na(x)
      if (sum(sel) >= min_n) return(stats::median(x[sel]))
    }
    NA_real_
  }, numeric(1))
}

# trailing mean over the last k operating days (including today)
trail_mean <- function(x, k = 3) {
  vapply(seq_along(x), function(i) mean(x[max(1, i - k + 1):i], na.rm = TRUE), numeric(1))
}

# Date of the most recent major fault strictly before each row (repairs reset baselines).
last_fault_before <- function(dates, fault_dates) {
  vapply(seq_along(dates), function(i) {
    f <- fault_dates[fault_dates < dates[i]]
    if (length(f) == 0) as.numeric(as.Date("1900-01-01")) else as.numeric(max(f))
  }, numeric(1))
}

build_features <- function(d, horizon = 7) {
  d$coolant_rel <- fleet_relative(d, "coolant_highload_c")
  d$fuelp_rel <- fleet_relative(d, "fuel_pressure_p05_kpa")
  d$econ_rel <- fleet_relative(d, "km_per_l")
  out <- lapply(split(d, d$vehicle), function(v) {
    v <- v[order(v$date), ]
    fault_dates <- v$date[v$major_dtc_events > 0]
    # first day of each fault episode (consecutive fault days count once)
    first_fault <- if (length(fault_dates) == 0) fault_dates else fault_dates[c(TRUE, diff(fault_dates) > 3)]
    repaired_after <- as.Date(last_fault_before(v$date, first_fault), origin = "1970-01-01")
    cool_base <- own_baseline(v$date, v$coolant_rel, repaired_after)
    fuel_base <- own_baseline(v$date, v$fuelp_rel, repaired_after)
    econ_base <- own_baseline(v$date, v$econ_rel, repaired_after)
    v$coolant_dev <- trail_mean(v$coolant_rel - cool_base)
    v$fuelp_dev <- trail_mean(v$fuelp_rel - fuel_base)
    v$econ_dev_pct <- trail_mean(100 * (v$econ_rel - econ_base) / stats::median(v$km_per_l, na.rm = TRUE))
    v$coolant_slope <- vapply(seq_len(nrow(v)), function(i) {
      sel <- v$date > v$date[i] - 8 & v$date <= v$date[i] & v$date > repaired_after[i]
      slope(v$date[sel], v$coolant_rel[sel])
    }, numeric(1))
    v$fuelp_slope <- vapply(seq_len(nrow(v)), function(i) {
      sel <- v$date > v$date[i] - 8 & v$date <= v$date[i] & v$date > repaired_after[i]
      slope(v$date[sel], v$fuelp_rel[sel])
    }, numeric(1))
    # label: a major fault starts within the next `horizon` days
    v$days_to_fault <- vapply(seq_len(nrow(v)), function(i) {
      nxt <- first_fault[first_fault > v$date[i]]
      if (length(nxt) == 0) NA_real_ else as.numeric(min(nxt) - v$date[i])
    }, numeric(1))
    v$y <- as.integer(!is.na(v$days_to_fault) & v$days_to_fault <= horizon)
    v$fault_day <- v$major_dtc_events > 0
    v
  })
  f <- do.call(rbind, out)
  rownames(f) <- NULL
  f
}

# rows usable for training/scoring: no fault in progress and all features present
usable <- function(f) !f$fault_day & stats::complete.cases(f[, FEATURES])
