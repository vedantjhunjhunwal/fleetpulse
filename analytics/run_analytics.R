#!/usr/bin/env Rscript
# FleetPulse analytics: predicts which trucks will have a cooling or fuel-system
# fault within 7 days, evaluates the model with truck-grouped cross-validation,
# writes a report and (optionally) posts the current risk scores to the API.
#
#   Rscript run_analytics.R <daily_features.csv> <out_dir> [api_url]

args <- commandArgs(trailingOnly = TRUE)
csv <- if (length(args) >= 1) args[1] else "out/daily_features.csv"
out <- if (length(args) >= 2) args[2] else "out"
api <- if (length(args) >= 3) args[3] else ""

here <- dirname(sub("--file=", "", grep("--file=", commandArgs(FALSE), value = TRUE)[1]))
source(file.path(here, "R", "features.R"))
source(file.path(here, "R", "model.R"))
dir.create(out, showWarnings = FALSE, recursive = TRUE)

LAMBDA <- 1
FPR <- 0.02
PERSIST <- 2  # days in a row at or above the threshold before a truck is flagged

d <- read_daily(csv)
f <- build_features(d)
cv <- cross_validate(f, k = 4, lambda = LAMBDA, fpr = FPR, persist = PERSIST)
p <- cv$predictions

# final model on every usable day, then score each truck's most recent usable day
u <- f[usable(f), ]
m <- fit_logit(u[, FEATURES], u$y, LAMBDA)
thr <- pick_threshold(predict_logit(m, u), u$y, FPR)
u$risk_day <- predict_logit(m, u)
# Score each truck on its most recent operating day only. Trucks whose latest day
# is not usable (fault in progress, or baseline not yet rebuilt after a repair)
# stay unscored; the API marks trucks with an active fault as "down" anyway.
# Confirmed risk = the lower of the last PERSIST days, so risk >= threshold is a confirmed alert.
last_day <- tapply(f$date, f$vehicle, max)
latest <- do.call(rbind, lapply(split(u, u$vehicle), function(v) {
  v <- v[order(v$date), ]
  if (v$date[nrow(v)] != as.Date(last_day[[v$vehicle[1]]], origin = "1970-01-01")) return(NULL)
  last <- utils::tail(v, PERSIST)
  row <- v[nrow(v), ]
  row$risk <- round(min(last$risk_day), 4)
  row
}))
latest$driver <- risk_driver(m, latest)
batch <- list(
  threshold = round(thr, 4),
  model = sprintf("L2 logistic regression, lambda=%g, %d features, 7-day horizon, %d-day confirmation", LAMBDA, length(FEATURES), PERSIST),
  generatedAt = format(Sys.time(), "%Y-%m-%dT%H:%M:%SZ", tz = "UTC"),
  scores = lapply(seq_len(nrow(latest)), function(i) list(
    vehicle = latest$vehicle[i], date = format(latest$date[i]), risk = latest$risk[i], driver = latest$driver[i]))
)
jsonlite::write_json(batch, file.path(out, "risk_scores.json"), auto_unbox = TRUE, pretty = TRUE, digits = NA)

leads <- cv$lead_days
metrics <- list(
  truck_days_scored = cv$truck_days,
  positives = sum(p$y),
  oof_auc = round(cv$auc, 4),
  failing_trucks = cv$failing_trucks,
  caught_before_fault = cv$caught,
  median_lead_days = stats::median(leads, na.rm = TRUE),
  min_lead_days = min(leads, na.rm = TRUE),
  lead_days = as.list(leads),
  false_alert_episodes = nrow(cv$false_alert_episodes),
  false_alerts_per_1000_truck_days = round(1000 * nrow(cv$false_alert_episodes) / cv$truck_days, 2),
  false_alert_trucks = unique(cv$false_alert_episodes$vehicle),
  coefficients = as.list(round(m$beta, 4)),
  threshold = round(thr, 4)
)
jsonlite::write_json(metrics, file.path(out, "metrics.json"), auto_unbox = TRUE, pretty = TRUE, digits = NA)

# risk timelines for the trucks that failed (out-of-fold risk)
grDevices::png(file.path(out, "risk_timeline.png"), width = 1100, height = 650, res = 110)
failing <- names(leads)
cols <- grDevices::hcl.colors(length(failing), "Dark 3")
plot(NA, xlim = c(-30, 0), ylim = c(0, 1), xlab = "Days before the fault (DM1)", ylab = "Out-of-fold risk",
     main = "FleetPulse: 7-day fault risk before each cooling / fuel failure")
ff <- do.call(rbind, lapply(split(f[f$fault_day, ], f$vehicle[f$fault_day]), function(x) x[which.min(x$date), ]))
for (i in seq_along(failing)) {
  v <- p[p$vehicle == failing[i], ]
  fd <- ff$date[ff$vehicle == failing[i]]
  x <- as.numeric(v$date - fd)
  keep <- x < 0 & x >= -30
  lines(x[keep], v$risk[keep], col = cols[i], lwd = 2)
}
abline(h = stats::median(p$threshold), lty = 2)
legend("topleft", legend = c(failing, "alert threshold (median of folds)"), col = c(cols, "black"),
       lty = c(rep(1, length(failing)), 2), lwd = 2, cex = 0.75, bty = "n")
invisible(grDevices::dev.off())

report <- c(
  "# FleetPulse predictive-maintenance report", "",
  sprintf("- Truck-days scored: %d (%d inside the 7 days before a fault)", cv$truck_days, sum(p$y)),
  sprintf("- Out-of-fold ROC AUC (4 folds grouped by truck): %.3f", cv$auc),
  sprintf("- Faults flagged in advance: %d of %d; median warning %.0f days (minimum %.0f)",
          cv$caught, cv$failing_trucks, metrics$median_lead_days, metrics$min_lead_days),
  sprintf("- Alert rule: risk at or above the threshold on %d operating days in a row", PERSIST),
  sprintf("- False alert episodes: %d (%.2f per 1,000 truck-days)%s", metrics$false_alert_episodes,
          metrics$false_alerts_per_1000_truck_days,
          if (length(metrics$false_alert_trucks)) paste0(" on ", paste(metrics$false_alert_trucks, collapse = ", ")) else ""),
  "", "## Warning time per failed truck", "",
  "| Truck | Days of warning |", "|---|---|",
  sprintf("| %s | %s |", names(leads), ifelse(is.na(leads), "missed", as.character(leads))),
  "", sprintf("## Current risk (final model, %d of %d trucks scored on their latest day)", nrow(latest), length(last_day)), "",
  "| Truck | Date | Risk | Driver |", "|---|---|---|---|",
  sprintf("| %s | %s | %.3f | %s |", latest$vehicle, format(latest$date), latest$risk, latest$driver)[order(-latest$risk)],
  "", sprintf("Alert threshold: %.4f (2%% single-day false-alarm rate on training days); risk shown is the lower of the last %d days.", thr, PERSIST)
)
writeLines(report, file.path(out, "report.md"))
cat(report[3:7], sep = "\n")

if (nzchar(api)) {
  res <- system2("curl", c("-sf", "-X", "POST", "-H", shQuote("Content-Type: application/json"),
                           "--data-binary", shQuote(paste0("@", file.path(out, "risk_scores.json"))),
                           shQuote(paste0(api, "/api/risk"))), stdout = TRUE)
  cat("posted risk scores:", res, "\n")
}
