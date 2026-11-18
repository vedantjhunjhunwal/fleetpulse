# Risk model and evaluation for FleetPulse.
#
# L2-regularised logistic regression (fitted with optim, so it stays stable
# when the classes are nearly separable), evaluated with cross-validation
# grouped by truck: a truck is never scored by a model that saw its own days.

standardise <- function(X, centre = NULL, scale = NULL) {
  if (is.null(centre)) centre <- colMeans(X)
  if (is.null(scale)) scale <- apply(X, 2, stats::sd)
  scale[scale == 0 | is.na(scale)] <- 1
  list(Z = sweep(sweep(X, 2, centre), 2, scale, "/"), centre = centre, scale = scale)
}

fit_logit <- function(X, y, lambda = 1) {
  s <- standardise(as.matrix(X))
  Z <- cbind(1, s$Z)
  nll <- function(b) {
    eta <- drop(Z %*% b)
    -sum(y * eta - log1p(exp(eta))) + lambda * sum(b[-1]^2)
  }
  grad <- function(b) {
    p <- stats::plogis(drop(Z %*% b))
    g <- -drop(crossprod(Z, y - p))
    g[-1] <- g[-1] + 2 * lambda * b[-1]
    g
  }
  opt <- stats::optim(rep(0, ncol(Z)), nll, grad, method = "BFGS", control = list(maxit = 500))
  if (opt$convergence != 0) warning("logistic regression did not converge")
  list(beta = stats::setNames(opt$par, c("(Intercept)", colnames(X))),
       centre = s$centre, scale = s$scale, features = colnames(X))
}

predict_logit <- function(m, X) {
  Z <- standardise(as.matrix(X[, m$features, drop = FALSE]), m$centre, m$scale)$Z
  stats::plogis(drop(cbind(1, Z) %*% m$beta))
}

# Which system drives the risk: sum of positive standardised contributions per group.
risk_driver <- function(m, X) {
  Z <- standardise(as.matrix(X[, m$features, drop = FALSE]), m$centre, m$scale)$Z
  contrib <- sweep(Z, 2, m$beta[-1], "*")
  cool <- rowSums(pmax(contrib[, intersect(COOLING_FEATURES, m$features), drop = FALSE], 0))
  fuel <- rowSums(pmax(contrib[, intersect(FUEL_FEATURES, m$features), drop = FALSE], 0))
  ifelse(cool >= fuel, "cooling", "fuel")
}

# Rank-based (Mann-Whitney) ROC AUC.
auc <- function(score, y) {
  pos <- score[y == 1]
  neg <- score[y == 0]
  if (length(pos) == 0 || length(neg) == 0) return(NA_real_)
  r <- rank(c(pos, neg))
  (sum(r[seq_along(pos)]) - length(pos) * (length(pos) + 1) / 2) / (length(pos) * length(neg))
}

# Deterministic grouped folds: failing trucks and healthy trucks are each dealt
# round-robin across the folds so every fold has some failures.
grouped_folds <- function(vehicles, failing, k = 4) {
  v <- sort(unique(vehicles))
  bad <- intersect(v, failing)
  good <- setdiff(v, failing)
  fold_of <- c(stats::setNames((seq_along(bad) - 1) %% k + 1, bad),
               stats::setNames((seq_along(good) - 1 + length(bad)) %% k + 1, good))
  unname(fold_of[vehicles])
}

# Threshold that keeps the false-alarm rate on training negatives at `fpr`.
pick_threshold <- function(risk, y, fpr = 0.02) unname(stats::quantile(risk[y == 0], 1 - fpr, type = 7))

# Confirmed alerts: risk at or above the threshold on `persist` consecutive
# operating days of the same truck (a single noisy day does not page anyone).
confirm_alerts <- function(vehicle, date, risk, threshold, persist = 2) {
  o <- order(vehicle, date)
  hit <- risk[o] >= threshold[o]
  conf <- stats::ave(as.numeric(hit), vehicle[o], FUN = function(a) {
    run <- 0
    vapply(a, function(x) { run <<- if (x == 1) run + 1 else 0; as.numeric(run >= persist) }, numeric(1))
  })
  out <- logical(length(risk))
  out[o] <- conf == 1
  out
}

# Alert episodes: runs of consecutive operating days at or above the threshold.
alert_episodes <- function(dates, alert) {
  if (!any(alert)) return(data.frame(start = as.Date(character(0)), end = as.Date(character(0))))
  idx <- which(alert)
  breaks <- c(TRUE, diff(idx) > 1)
  starts <- idx[breaks]
  ends <- idx[c(breaks[-1], TRUE)]
  data.frame(start = dates[starts], end = dates[ends])
}

cross_validate <- function(f, k = 4, lambda = 1, fpr = 0.02, window = 14, persist = 2) {
  u <- f[usable(f), ]
  failing <- unique(u$vehicle[u$y == 1])
  u$fold <- grouped_folds(u$vehicle, failing, k)
  u$risk <- NA_real_
  u$threshold <- NA_real_
  for (i in seq_len(k)) {
    tr <- u$fold != i
    m <- fit_logit(u[tr, FEATURES], u$y[tr], lambda)
    thr <- pick_threshold(predict_logit(m, u[tr, ]), u$y[tr], fpr)
    u$risk[!tr] <- predict_logit(m, u[!tr, ])
    u$threshold[!tr] <- thr
  }
  u$alert <- confirm_alerts(u$vehicle, u$date, u$risk, u$threshold, persist)

  # per-truck outcomes
  faults <- unique(f[f$fault_day, c("vehicle", "date")])
  first_faults <- do.call(rbind, lapply(split(faults, faults$vehicle), function(x) {
    x <- x[order(x$date), ]
    x[c(TRUE, diff(x$date) > 3), ]
  }))
  first_faults <- first_faults[first_faults$vehicle %in% unique(f$vehicle[f$y == 1]), ]
  leads <- vapply(seq_len(nrow(first_faults)), function(i) {
    v <- u[u$vehicle == first_faults$vehicle[i], ]
    pre <- v[v$date < first_faults$date[i] & v$date >= first_faults$date[i] - window, ]
    if (!any(pre$alert)) return(NA_real_)
    ep <- alert_episodes(pre$date, pre$alert)
    as.numeric(first_faults$date[i] - ep$start[nrow(ep)])  # episode that runs into the fault
  }, numeric(1))

  # false alerts: episodes that do not lead into a fault within `window` days
  false_eps <- do.call(rbind, lapply(split(u, u$vehicle), function(v) {
    ep <- alert_episodes(v$date, v$alert)
    if (nrow(ep) == 0) return(NULL)
    ff <- first_faults$date[first_faults$vehicle == v$vehicle[1]]
    true_ep <- vapply(seq_len(nrow(ep)), function(j) any(ff > ep$end[j] & ff - ep$end[j] <= window), logical(1))
    if (all(true_ep)) return(NULL)
    data.frame(vehicle = v$vehicle[1], start = ep$start[!true_ep], end = ep$end[!true_ep])
  }))
  if (is.null(false_eps)) false_eps <- data.frame(vehicle = character(0), start = as.Date(character(0)), end = as.Date(character(0)))

  list(
    predictions = u,
    auc = auc(u$risk, u$y),
    failing_trucks = nrow(first_faults),
    caught = sum(!is.na(leads)),
    lead_days = stats::setNames(leads, first_faults$vehicle),
    false_alert_episodes = false_eps,
    truck_days = nrow(u)
  )
}
