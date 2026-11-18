source(file.path("..", "..", "R", "features.R"))
source(file.path("..", "..", "R", "model.R"))

day <- function(i) as.Date("2026-08-03") + i

# REQ: FR-11
test_that("fleet_relative removes a shift shared by every truck that day", {
  d <- data.frame(vehicle = rep(c("a", "b", "c"), 2), date = rep(day(0:1), each = 3),
                  x = c(90, 91, 95, 96, 97, 101))  # day 2 is a heatwave: everyone +6
  r <- fleet_relative(d, "x")
  expect_equal(r[1:3], r[4:6])
  expect_equal(r[1:3], c(-1, 0, 4))
})

# REQ: FR-11
test_that("slope is the OLS slope per day and needs three points", {
  expect_equal(slope(day(0:4), c(1, 3, 5, 7, 9)), 2)
  expect_true(is.na(slope(day(0:1), c(1, 2))))
  expect_true(is.na(slope(day(0:4), c(1, NA, NA, NA, 2))))
})

# REQ: FR-11
test_that("own_baseline uses the history window and restarts after a repair", {
  dates <- day(0:40)
  x <- c(rep(0, 10), rep(10, 31))
  none <- rep(as.Date("1900-01-01"), 41)
  b <- own_baseline(dates, x, none)
  expect_true(is.na(b[1]))                  # nothing earlier than today - 3
  expect_equal(b[15], 0)                    # day 14: days 0..4 in the long window
  expect_equal(b[41], 10)                   # day 40: days 5..30, mostly the new level
  repaired <- rep(day(25), 41)              # repair on day 25: only later days count
  b2 <- own_baseline(dates, x, repaired)
  expect_true(is.na(b2[28]))                # days 26..27 only
  expect_equal(b2[33], 10)                  # short window days 26..29
})

# REQ: FR-11
test_that("labels mark the 7 days before the first day of a fault episode", {
  n <- 30
  d <- data.frame(vehicle = "trk01", date = day(0:(n - 1)), km_per_l = 2.5,
                  coolant_highload_c = 90, fuel_pressure_p05_kpa = 480,
                  major_dtc_events = 0)
  d$major_dtc_events[d$date %in% day(20:21)] <- 5
  f <- build_features(d)
  expect_equal(which(f$y == 1), 14:20)       # days 13..19 are 1..7 days before day 20
  expect_equal(f$days_to_fault[14], 7)
  expect_equal(f$y[13], 0L)                 # 8 days ahead is outside the horizon
  expect_true(all(f$fault_day[21:22]))
  expect_false(any(usable(f)[21:22]))
})

# REQ: FR-11
test_that("auc matches hand-computed values", {
  expect_equal(auc(c(0.9, 0.8, 0.1, 0.2), c(1, 1, 0, 0)), 1)
  expect_equal(auc(c(0.1, 0.2, 0.9, 0.8), c(1, 1, 0, 0)), 0)
  expect_equal(auc(c(0.5, 0.5), c(1, 0)), 0.5)
  expect_true(is.na(auc(c(0.5, 0.6), c(0, 0))))
})

# REQ: FR-11
test_that("logistic regression recovers direction and stays finite when separable", {
  set.seed(1)
  x <- c(rnorm(50, -2), rnorm(50, 2))
  y <- rep(0:1, each = 50)
  m <- fit_logit(data.frame(x = x), y, lambda = 1)
  expect_gt(m$beta[["x"]], 0)
  expect_true(all(is.finite(m$beta)))
  p <- predict_logit(m, data.frame(x = c(-3, 3)))
  expect_lt(p[1], 0.05)
  expect_gt(p[2], 0.95)
})

# REQ: FR-11
test_that("grouped folds keep each truck in one fold and spread the failures", {
  veh <- rep(sprintf("trk%02d", 1:8), each = 3)
  folds <- grouped_folds(veh, failing = c("trk01", "trk02", "trk03", "trk04"), k = 4)
  expect_equal(length(unique(paste(veh, folds))), 8)
  bad_folds <- unique(folds[veh %in% c("trk01", "trk02", "trk03", "trk04")])
  expect_setequal(bad_folds, 1:4)
})

# REQ: FR-11
test_that("alerts need two days in a row and episodes are counted per run", {
  veh <- rep("a", 8)
  dates <- day(0:7)
  risk <- c(0.9, 0.1, 0.9, 0.9, 0.9, 0.1, 0.9, 0.9)
  a <- confirm_alerts(veh, dates, risk, rep(0.5, 8), persist = 2)
  expect_equal(a, c(FALSE, FALSE, FALSE, TRUE, TRUE, FALSE, FALSE, TRUE))
  ep <- alert_episodes(dates, a)
  expect_equal(nrow(ep), 2)
  expect_equal(ep$start, day(c(3, 7)))
  expect_equal(nrow(alert_episodes(dates, rep(FALSE, 8))), 0)
})

# REQ: FR-11
test_that("pick_threshold gives the requested false-alarm rate on negatives", {
  risk <- c(seq(0, 0.99, by = 0.01), 0.995)
  y <- c(rep(0, 100), 1)
  thr <- pick_threshold(risk, y, fpr = 0.02)
  expect_equal(mean(risk[y == 0] >= thr), 0.02, tolerance = 0.011)
})

# REQ: FR-11, FR-12
test_that("risk driver names the system that pushes the risk up", {
  X <- data.frame(coolant_dev = c(0, 8, 0), coolant_slope = c(0, 1, 0),
                  fuelp_dev = c(0, 0, -90), fuelp_slope = c(0, 0, -12), econ_dev_pct = c(0, 0, -3))
  m <- list(beta = c(-5, 1.5, 1.3, -1.4, -1.2, 0.4), centre = colMeans(X) * 0, scale = rep(1, 5),
            features = colnames(X))
  expect_equal(risk_driver(m, X)[2:3], c("cooling", "fuel"))
})
