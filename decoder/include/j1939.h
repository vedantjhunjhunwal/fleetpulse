/*
 * j1939.h - SAE J1939 telemetry decoding for heavy-duty trucks.
 *
 * Parses candump-format CAN log lines, extracts the PGN / source address from
 * 29-bit identifiers, decodes the engine and vehicle signals FleetPulse uses
 * (J1939-71 scaling), decodes DM1 active diagnostic trouble codes (J1939-73)
 * and reassembles multi-packet messages sent with the BAM transport protocol
 * (J1939-21).
 */
#ifndef FLEETPULSE_J1939_H
#define FLEETPULSE_J1939_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define J1939_API __declspec(dllexport)
#else
#define J1939_API __attribute__((visibility("default")))
#endif

#define J1939_VEHICLE_MAX 16
#define J1939_TP_MAX_BYTES 1785 /* 255 packets x 7 bytes */

/* Parameter Group Numbers used by FleetPulse */
#define PGN_EEC1 61444u   /* 0xF004 Electronic Engine Controller 1 */
#define PGN_CCVS 65265u   /* 0xFEF1 Cruise Control / Vehicle Speed */
#define PGN_ET1 65262u    /* 0xFEEE Engine Temperature 1 */
#define PGN_LFE 65266u    /* 0xFEF2 Fuel Economy (Liquid) */
#define PGN_EFLP1 65263u  /* 0xFEEF Engine Fluid Level/Pressure 1 */
#define PGN_DM1 65226u    /* 0xFECA Active Diagnostic Trouble Codes */
#define PGN_TP_CM 60416u  /* 0xEC00 Transport Protocol - Connection Mgmt */
#define PGN_TP_DT 60160u  /* 0xEB00 Transport Protocol - Data Transfer */

/* Error codes (negative) */
enum {
    J1939_OK = 0,
    J1939_ERR_NULL = -1,
    J1939_ERR_FORMAT = -2,   /* line is not "(ts) iface ID#DATA" */
    J1939_ERR_ID = -3,       /* identifier is not a 29-bit hex id */
    J1939_ERR_DATA = -4,     /* odd number of hex digits / bad hex / > 8 bytes */
    J1939_ERR_TP = -5        /* transport protocol sequence error */
};

typedef struct {
    double ts;                       /* seconds since epoch */
    char vehicle[J1939_VEHICLE_MAX]; /* candump interface name = vehicle id */
    uint32_t can_id;                 /* 29-bit identifier */
    uint8_t len;                     /* 0..8 */
    uint8_t data[8];
} j1939_frame;

typedef enum {
    SIG_ENGINE_SPEED = 0, /* SPN 190, rpm        */
    SIG_VEHICLE_SPEED,    /* SPN 84,  km/h       */
    SIG_COOLANT_TEMP,     /* SPN 110, deg C      */
    SIG_FUEL_RATE,        /* SPN 183, L/h        */
    SIG_FUEL_ECONOMY,     /* SPN 184, km/L       */
    SIG_FUEL_PRESSURE,    /* SPN 94,  kPa        */
    SIG_OIL_PRESSURE,     /* SPN 100, kPa        */
    SIG_COUNT
} j1939_signal_id;

typedef struct {
    uint32_t spn; /* 19-bit Suspect Parameter Number */
    uint8_t fmi;  /* Failure Mode Identifier, 0..31 */
    uint8_t oc;   /* occurrence count, 0..126 */
} j1939_dtc;

enum { J1939_EVT_SIGNAL = 1, J1939_EVT_DTC = 2, J1939_EVT_DTC_NONE = 3 };

/* Flat event record; layout is mirrored by the C# P/Invoke struct. */
typedef struct {
    double ts;
    double value;   /* signal value (EVT_SIGNAL) */
    uint32_t spn;   /* SPN of the signal or of the DTC */
    int32_t kind;   /* J1939_EVT_* */
    int32_t signal; /* j1939_signal_id, or -1 for DTC events */
    uint8_t fmi;
    uint8_t oc;
    uint8_t sa;     /* source address of the sending ECU */
    uint8_t lamp;   /* DM1 lamp bits: 1 = amber warning, 2 = red stop, 4 = MIL, 8 = protect */
    char vehicle[J1939_VEHICLE_MAX];
} j1939_event;

/* Identifier helpers */
J1939_API uint32_t j1939_pgn(uint32_t can_id);
J1939_API uint8_t j1939_source_address(uint32_t can_id);
J1939_API uint8_t j1939_priority(uint32_t can_id);

/* Parse one candump line, e.g. "(1700000000.250000) trk07 18FEEE00#7A..." */
J1939_API int j1939_parse_candump(const char *line, j1939_frame *out);

/* Decode the signals carried by a single-frame PGN. Returns the number of
 * signals written (0 if the PGN is not one FleetPulse decodes). Signals whose
 * raw value is "error" or "not available" are skipped. */
J1939_API int j1939_decode_signals(const j1939_frame *f, j1939_event *out, int max);

/* Decode a DM1 payload (any length >= 6). Returns the number of DTCs written,
 * 0 for the "no active DTC" pattern. lamp_bits receives the J1939_EVT lamp mask. */
J1939_API int j1939_decode_dm1(const uint8_t *data, size_t len, uint8_t *lamp_bits,
                               j1939_dtc *out, int max);

/* Session: per-source-address BAM reassembly + full event decoding. */
typedef struct j1939_session j1939_session;
J1939_API j1939_session *j1939_session_new(void);
J1939_API void j1939_session_free(j1939_session *s);

/* Feed one candump line. Returns number of events written (>= 0) or a
 * negative J1939_ERR_* code. Counters are kept for lines/errors. */
J1939_API int j1939_session_feed_line(j1939_session *s, const char *line, j1939_event *out, int max);

typedef struct {
    uint64_t lines;
    uint64_t frames;
    uint64_t parse_errors;
    uint64_t tp_errors;
    uint64_t events;
    uint64_t tp_messages; /* completed BAM messages */
} j1939_stats;
J1939_API void j1939_session_stats(const j1939_session *s, j1939_stats *out);

J1939_API const char *j1939_signal_name(int signal);
J1939_API const char *j1939_version(void);

#ifdef __cplusplus
}
#endif
#endif
