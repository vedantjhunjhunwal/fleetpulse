/* j1939.c - see j1939.h */
#include "j1939.h"

#include <ctype.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define TP_SLOTS 64
#define TP_TIMEOUT_S 0.75 /* J1939-21 T1: max gap between BAM data packets */

const char *j1939_version(void) { return "1.0.0"; }

uint32_t j1939_pgn(uint32_t id) {
    uint32_t edp_dp = (id >> 24) & 0x3u;
    uint32_t pf = (id >> 16) & 0xFFu;
    uint32_t ps = (id >> 8) & 0xFFu;
    /* PDU1 (PF < 240): PS is a destination address and not part of the PGN */
    return (edp_dp << 16) | (pf << 8) | (pf < 240 ? 0u : ps);
}

uint8_t j1939_source_address(uint32_t id) { return (uint8_t)(id & 0xFFu); }
uint8_t j1939_priority(uint32_t id) { return (uint8_t)((id >> 26) & 0x7u); }

static int hexval(int c) {
    if (c >= '0' && c <= '9') return c - '0';
    c = tolower(c);
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return -1;
}

int j1939_parse_candump(const char *line, j1939_frame *out) {
    if (!line || !out) return J1939_ERR_NULL;
    memset(out, 0, sizeof *out);
    const char *p = line;
    while (*p == ' ' || *p == '\t') p++;
    if (*p != '(') return J1939_ERR_FORMAT;
    char *end = NULL;
    double ts = strtod(p + 1, &end);
    if (end == p + 1 || *end != ')' || !isfinite(ts) || ts < 0) return J1939_ERR_FORMAT;
    p = end + 1;
    if (*p != ' ' && *p != '\t') return J1939_ERR_FORMAT;
    while (*p == ' ' || *p == '\t') p++;

    /* interface name -> vehicle id */
    size_t n = 0;
    while (p[n] && p[n] != ' ' && p[n] != '\t') n++;
    if (n == 0 || n >= J1939_VEHICLE_MAX) return J1939_ERR_FORMAT;
    memcpy(out->vehicle, p, n);
    out->vehicle[n] = '\0';
    p += n;
    while (*p == ' ' || *p == '\t') p++;

    /* identifier: exactly 8 hex digits (extended frame) followed by '#' */
    uint32_t id = 0;
    int digits = 0;
    while (*p && *p != '#') {
        int v = hexval((unsigned char)*p);
        if (v < 0 || digits == 8) return J1939_ERR_ID;
        id = (id << 4) | (uint32_t)v;
        digits++;
        p++;
    }
    if (*p != '#') return J1939_ERR_FORMAT;
    if (digits != 8 || id > 0x1FFFFFFFu) return J1939_ERR_ID;
    p++;

    int len = 0;
    while (*p && *p != '\n' && *p != '\r' && *p != ' ') {
        int hi = hexval((unsigned char)p[0]);
        int lo = p[1] ? hexval((unsigned char)p[1]) : -1;
        if (hi < 0 || lo < 0 || len == 8) return J1939_ERR_DATA;
        out->data[len++] = (uint8_t)((hi << 4) | lo);
        p += 2;
    }
    out->ts = ts;
    out->can_id = id;
    out->len = (uint8_t)len;
    return J1939_OK;
}

/* ---- signal decoding (J1939-71 resolution / offset) ---- */

static int u8_valid(uint8_t raw) { return raw <= 0xFAu; }
static int u16_valid(uint16_t raw) { return raw <= 0xFAFFu; }
static uint16_t le16(const uint8_t *d) { return (uint16_t)(d[0] | (d[1] << 8)); }

static void put_signal(j1939_event *e, const j1939_frame *f, int sig, uint32_t spn, double v) {
    memset(e, 0, sizeof *e);
    e->ts = f->ts;
    e->value = v;
    e->spn = spn;
    e->kind = J1939_EVT_SIGNAL;
    e->signal = sig;
    e->sa = j1939_source_address(f->can_id);
    memcpy(e->vehicle, f->vehicle, J1939_VEHICLE_MAX);
}

#define EMIT(sig, spn, v)                                  \
    do {                                                   \
        if (n < max) put_signal(&out[n++], f, sig, spn, v); \
    } while (0)

int j1939_decode_signals(const j1939_frame *f, j1939_event *out, int max) {
    if (!f || !out || max <= 0) return 0;
    const uint8_t *d = f->data;
    int n = 0;
    switch (j1939_pgn(f->can_id)) {
    case PGN_EEC1:
        if (f->len >= 5 && u16_valid(le16(d + 3))) EMIT(SIG_ENGINE_SPEED, 190, le16(d + 3) * 0.125);
        break;
    case PGN_CCVS:
        if (f->len >= 3 && u16_valid(le16(d + 1))) EMIT(SIG_VEHICLE_SPEED, 84, le16(d + 1) / 256.0);
        break;
    case PGN_ET1:
        if (f->len >= 1 && u8_valid(d[0])) EMIT(SIG_COOLANT_TEMP, 110, (double)d[0] - 40.0);
        break;
    case PGN_LFE:
        if (f->len >= 2 && u16_valid(le16(d))) EMIT(SIG_FUEL_RATE, 183, le16(d) * 0.05);
        if (f->len >= 4 && u16_valid(le16(d + 2))) EMIT(SIG_FUEL_ECONOMY, 184, le16(d + 2) / 512.0);
        break;
    case PGN_EFLP1:
        if (f->len >= 1 && u8_valid(d[0])) EMIT(SIG_FUEL_PRESSURE, 94, d[0] * 4.0);
        if (f->len >= 4 && u8_valid(d[3])) EMIT(SIG_OIL_PRESSURE, 100, d[3] * 4.0);
        break;
    default:
        break;
    }
    return n;
}

int j1939_decode_dm1(const uint8_t *d, size_t len, uint8_t *lamp_bits, j1939_dtc *out, int max) {
    if (!d || len < 6) return 0;
    uint8_t lamps = 0;
    if (((d[0] >> 2) & 3u) == 1u) lamps |= 1; /* amber warning */
    if (((d[0] >> 4) & 3u) == 1u) lamps |= 2; /* red stop */
    if (((d[0] >> 6) & 3u) == 1u) lamps |= 4; /* malfunction indicator */
    if ((d[0] & 3u) == 1u) lamps |= 8;        /* protect */
    if (lamp_bits) *lamp_bits = lamps;
    int n = 0;
    for (size_t off = 2; off + 4 <= len && n < max; off += 4) {
        const uint8_t *t = d + off;
        uint32_t spn = (uint32_t)t[0] | ((uint32_t)t[1] << 8) | ((uint32_t)(t[2] & 0xE0u) << 11);
        uint8_t fmi = t[2] & 0x1Fu;
        if (spn == 0 && fmi == 0) continue;           /* "no active DTC" placeholder */
        if (spn == 0x7FFFFu && fmi == 0x1Fu) continue; /* 0xFF padding */
        out[n].spn = spn;
        out[n].fmi = fmi;
        out[n].oc = t[3] & 0x7Fu;
        n++;
    }
    return n;
}

/* ---- session: transport protocol (BAM) reassembly ---- */

typedef struct {
    int active;
    char vehicle[J1939_VEHICLE_MAX];
    uint8_t sa;
    uint32_t pgn;
    uint16_t total;
    uint8_t packets;
    uint8_t next_seq;
    double last_ts;
    uint8_t buf[J1939_TP_MAX_BYTES];
} tp_slot;

struct j1939_session {
    tp_slot slots[TP_SLOTS];
    j1939_stats stats;
};

j1939_session *j1939_session_new(void) { return (j1939_session *)calloc(1, sizeof(j1939_session)); }
void j1939_session_free(j1939_session *s) { free(s); }

void j1939_session_stats(const j1939_session *s, j1939_stats *out) {
    if (s && out) *out = s->stats;
}

static tp_slot *find_slot(j1939_session *s, const char *vehicle, uint8_t sa, int create) {
    tp_slot *free_slot = NULL;
    for (int i = 0; i < TP_SLOTS; i++) {
        tp_slot *t = &s->slots[i];
        if (t->active && t->sa == sa && strcmp(t->vehicle, vehicle) == 0) return t;
        if (!t->active && !free_slot) free_slot = t;
    }
    if (!create) return NULL;
    if (!free_slot) free_slot = &s->slots[0]; /* table full: evict oldest-index slot */
    memset(free_slot, 0, offsetof(tp_slot, buf));
    return free_slot;
}

static int dm1_events(const j1939_frame *f, const uint8_t *d, size_t len, j1939_event *out, int max) {
    j1939_dtc dtcs[64];
    uint8_t lamps = 0;
    int k = j1939_decode_dm1(d, len, &lamps, dtcs, 64);
    int n = 0;
    j1939_event e;
    memset(&e, 0, sizeof e);
    e.ts = f->ts;
    e.signal = -1;
    e.sa = j1939_source_address(f->can_id);
    e.lamp = lamps;
    memcpy(e.vehicle, f->vehicle, J1939_VEHICLE_MAX);
    if (k == 0) {
        e.kind = J1939_EVT_DTC_NONE;
        if (n < max) out[n++] = e;
        return n;
    }
    for (int i = 0; i < k && n < max; i++) {
        e.kind = J1939_EVT_DTC;
        e.spn = dtcs[i].spn;
        e.fmi = dtcs[i].fmi;
        e.oc = dtcs[i].oc;
        out[n++] = e;
    }
    return n;
}

int j1939_session_feed_line(j1939_session *s, const char *line, j1939_event *out, int max) {
    if (!s || !line || !out) return J1939_ERR_NULL;
    s->stats.lines++;
    j1939_frame f;
    int rc = j1939_parse_candump(line, &f);
    if (rc != J1939_OK) {
        s->stats.parse_errors++;
        return rc;
    }
    s->stats.frames++;
    uint32_t pgn = j1939_pgn(f.can_id);
    uint8_t sa = j1939_source_address(f.can_id);
    int n = 0;

    if (pgn == PGN_TP_CM) {
        if (f.len < 8 || f.data[0] != 32) return 0; /* only BAM (control byte 32) is broadcast */
        uint16_t total = le16(f.data + 1);
        uint8_t packets = f.data[3];
        tp_slot *t = find_slot(s, f.vehicle, sa, 1);
        if (t->active) s->stats.tp_errors++; /* new BAM aborts an unfinished one */
        if (total < 9 || total > J1939_TP_MAX_BYTES || packets != (total + 6) / 7) {
            t->active = 0;
            s->stats.tp_errors++;
            return J1939_ERR_TP;
        }
        t->active = 1;
        memcpy(t->vehicle, f.vehicle, J1939_VEHICLE_MAX);
        t->sa = sa;
        t->total = total;
        t->packets = packets;
        t->next_seq = 1;
        t->pgn = (uint32_t)f.data[5] | ((uint32_t)f.data[6] << 8) | ((uint32_t)f.data[7] << 16);
        t->last_ts = f.ts;
        memset(t->buf, 0xFF, sizeof t->buf);
        return 0;
    }
    if (pgn == PGN_TP_DT) {
        tp_slot *t = find_slot(s, f.vehicle, sa, 0);
        if (!t || f.len < 2) {
            s->stats.tp_errors++;
            return J1939_ERR_TP;
        }
        if (f.data[0] != t->next_seq || f.ts - t->last_ts > TP_TIMEOUT_S) {
            t->active = 0;
            s->stats.tp_errors++;
            return J1939_ERR_TP;
        }
        size_t off = (size_t)(f.data[0] - 1) * 7;
        memcpy(t->buf + off, f.data + 1, (size_t)f.len - 1);
        t->last_ts = f.ts;
        t->next_seq++;
        if (f.data[0] < t->packets) return 0;
        t->active = 0;
        s->stats.tp_messages++;
        if (t->pgn == PGN_DM1) n = dm1_events(&f, t->buf, t->total, out, max);
        s->stats.events += (uint64_t)n;
        return n;
    }
    if (pgn == PGN_DM1) {
        n = dm1_events(&f, f.data, f.len, out, max);
    } else {
        n = j1939_decode_signals(&f, out, max);
    }
    s->stats.events += (uint64_t)n;
    return n;
}

static const char *SIG_NAMES[SIG_COUNT] = {"engine_speed_rpm", "vehicle_speed_kmh", "coolant_temp_c",
                                           "fuel_rate_lph",    "fuel_economy_kmpl", "fuel_pressure_kpa",
                                           "oil_pressure_kpa"};

const char *j1939_signal_name(int signal) {
    return (signal >= 0 && signal < SIG_COUNT) ? SIG_NAMES[signal] : "dtc";
}
