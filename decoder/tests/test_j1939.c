/* Unit tests for the J1939 decoder. Each test names the requirement(s) it
 * verifies (docs/REQUIREMENTS.md); tools/trace.py checks the coverage.
 * REQ: NFR-02 - CI also runs this whole suite under AddressSanitizer + UBSan. */
#include "j1939.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static int failures = 0, checks = 0, tests = 0;

#define CHECK(cond)                                                       \
    do {                                                                  \
        checks++;                                                         \
        if (!(cond)) {                                                    \
            failures++;                                                   \
            fprintf(stderr, "  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); \
        }                                                                 \
    } while (0)
#define NEAR(a, b) CHECK(fabs((a) - (b)) < 1e-9)
#define TEST(name) static void name(void)
#define RUN(name)              \
    do {                       \
        tests++;               \
        printf("- %s\n", #name); \
        name();                \
    } while (0)

/* REQ: FR-01 */
TEST(parses_valid_candump_line) {
    j1939_frame f;
    CHECK(j1939_parse_candump("(1700000000.250000) trk07 18FEEE00#7AFFFFFFFFFFFFFF\n", &f) == J1939_OK);
    NEAR(f.ts, 1700000000.25);
    CHECK(strcmp(f.vehicle, "trk07") == 0);
    CHECK(f.can_id == 0x18FEEE00u);
    CHECK(f.len == 8);
    CHECK(f.data[0] == 0x7A && f.data[7] == 0xFF);
}

/* REQ: FR-01 */
TEST(accepts_short_frames_and_lowercase_hex) {
    j1939_frame f;
    CHECK(j1939_parse_candump("(5.0) trk01 18fef100#ff0040", &f) == J1939_OK);
    CHECK(f.len == 3 && f.data[2] == 0x40);
    CHECK(j1939_parse_candump("(5.0) trk01 18FEF100#", &f) == J1939_OK);
    CHECK(f.len == 0);
}

/* REQ: FR-01 */
TEST(rejects_malformed_lines) {
    j1939_frame f;
    CHECK(j1939_parse_candump("", &f) == J1939_ERR_FORMAT);
    CHECK(j1939_parse_candump("1700000000.25 trk07 18FEEE00#7A", &f) == J1939_ERR_FORMAT);   /* no () */
    CHECK(j1939_parse_candump("(abc) trk07 18FEEE00#7A", &f) == J1939_ERR_FORMAT);
    CHECK(j1939_parse_candump("(1.0) trk07 18FEEE00 7A", &f) == J1939_ERR_ID);                /* no # */
    CHECK(j1939_parse_candump("(1.0) trk07 7DF#0201", &f) == J1939_ERR_ID);                   /* 11-bit id */
    CHECK(j1939_parse_candump("(1.0) trk07 3FFFFFFF#00", &f) == J1939_ERR_ID);                /* > 29 bits */
    CHECK(j1939_parse_candump("(1.0) trk07 18FEEE00#7", &f) == J1939_ERR_DATA);               /* odd digits */
    CHECK(j1939_parse_candump("(1.0) trk07 18FEEE00#0011223344556677889", &f) == J1939_ERR_DATA); /* > 8 bytes */
    CHECK(j1939_parse_candump("(1.0) a_vehicle_name_too_long 18FEEE00#00", &f) == J1939_ERR_FORMAT);
    CHECK(j1939_parse_candump(NULL, &f) == J1939_ERR_NULL);
}

/* REQ: FR-02 */
TEST(extracts_pgn_source_and_priority) {
    CHECK(j1939_pgn(0x0CF00400u) == PGN_EEC1);
    CHECK(j1939_priority(0x0CF00400u) == 3);
    CHECK(j1939_source_address(0x0CF00400u) == 0x00);
    CHECK(j1939_pgn(0x18FEEE00u) == PGN_ET1);
    CHECK(j1939_pgn(0x18FECA03u) == PGN_DM1);
    CHECK(j1939_source_address(0x18FECA03u) == 0x03);
    /* PDU1: destination address is not part of the PGN */
    CHECK(j1939_pgn(0x1CECFF00u) == PGN_TP_CM);
    CHECK(j1939_pgn(0x1CEC2100u) == PGN_TP_CM);
    CHECK(j1939_pgn(0x1CEBFF00u) == PGN_TP_DT);
    CHECK(j1939_priority(0x1CEBFF00u) == 7);
}

static int decode_line(const char *line, j1939_event *ev, int max) {
    j1939_frame f;
    if (j1939_parse_candump(line, &f) != J1939_OK) return -1;
    return j1939_decode_signals(&f, ev, max);
}

/* REQ: FR-03 */
TEST(decodes_engine_speed_and_vehicle_speed) {
    j1939_event ev[4];
    /* EEC1 bytes 4-5 = 0x3200 = 12800 * 0.125 = 1600 rpm */
    CHECK(decode_line("(1.0) trk01 0CF00400#FFFFFF0032FFFFFF", ev, 4) == 1);
    CHECK(ev[0].signal == SIG_ENGINE_SPEED && ev[0].spn == 190);
    NEAR(ev[0].value, 1600.0);
    /* CCVS bytes 2-3 = 0x5500 = 21760 / 256 = 85 km/h */
    CHECK(decode_line("(1.0) trk01 18FEF100#FF0055FFFFFFFFFF", ev, 4) == 1);
    CHECK(ev[0].signal == SIG_VEHICLE_SPEED && ev[0].spn == 84);
    NEAR(ev[0].value, 85.0);
}

/* REQ: FR-03 */
TEST(decodes_temperature_fuel_and_pressure) {
    j1939_event ev[4];
    /* ET1 byte 1 = 0x7A = 122 - 40 = 82 C */
    CHECK(decode_line("(1.0) trk01 18FEEE00#7AFFFFFFFFFFFFFF", ev, 4) == 1);
    NEAR(ev[0].value, 82.0);
    /* LFE: fuel rate 0x02BC = 700 * 0.05 = 35 L/h ; economy 0x0600 = 1536 / 512 = 3 km/L */
    CHECK(decode_line("(1.0) trk01 18FEF200#BC020006FFFFFFFF", ev, 4) == 2);
    CHECK(ev[0].signal == SIG_FUEL_RATE);
    NEAR(ev[0].value, 35.0);
    CHECK(ev[1].signal == SIG_FUEL_ECONOMY);
    NEAR(ev[1].value, 3.0);
    /* EFL/P1: fuel delivery 0x96 = 150 * 4 = 600 kPa ; oil 0x64 = 100 * 4 = 400 kPa */
    CHECK(decode_line("(1.0) trk01 18FEEF00#96FFFF64FFFFFFFF", ev, 4) == 2);
    CHECK(ev[0].signal == SIG_FUEL_PRESSURE && ev[0].spn == 94);
    NEAR(ev[0].value, 600.0);
    CHECK(ev[1].signal == SIG_OIL_PRESSURE && ev[1].spn == 100);
    NEAR(ev[1].value, 400.0);
    CHECK(strcmp(ev[1].vehicle, "trk01") == 0);
}

/* REQ: FR-03 */
TEST(ignores_unknown_pgns_and_short_frames) {
    j1939_event ev[4];
    CHECK(decode_line("(1.0) trk01 18FEE900#0011223344556677", ev, 4) == 0); /* fuel consumption: not decoded */
    CHECK(decode_line("(1.0) trk01 0CF00400#FFFFFF00", ev, 4) == 0);          /* EEC1 too short for SPN 190 */
}

/* REQ: FR-04 */
TEST(skips_error_and_not_available_values) {
    j1939_event ev[4];
    CHECK(decode_line("(1.0) trk01 18FEEE00#FFFFFFFFFFFFFFFF", ev, 4) == 0); /* NA */
    CHECK(decode_line("(1.0) trk01 18FEEE00#FEFFFFFFFFFFFFFF", ev, 4) == 0); /* error */
    CHECK(decode_line("(1.0) trk01 18FEEE00#FBFFFFFFFFFFFFFF", ev, 4) == 0); /* reserved */
    CHECK(decode_line("(1.0) trk01 18FEEE00#FAFFFFFFFFFFFFFF", ev, 4) == 1); /* 250 - 40 = 210 is valid */
    CHECK(decode_line("(1.0) trk01 0CF00400#FFFFFF00FEFFFFFF", ev, 4) == 0); /* 2-byte error 0xFE00 */
    CHECK(decode_line("(1.0) trk01 0CF00400#FFFFFFFFFAFFFFFF", ev, 4) == 1); /* 0xFAFF max valid */
    /* LFE with fuel rate missing but economy present */
    CHECK(decode_line("(1.0) trk01 18FEF200#FFFF0006FFFFFFFF", ev, 4) == 1);
    CHECK(ev[0].signal == SIG_FUEL_ECONOMY);
}

/* REQ: FR-05 */
TEST(decodes_single_dm1_dtc_and_lamps) {
    /* amber lamp on (bits 4-3 = 01), SPN 110 FMI 0 OC 3 */
    const uint8_t d[8] = {0x04, 0xFF, 0x6E, 0x00, 0x00, 0x03, 0xFF, 0xFF};
    j1939_dtc dtc[4];
    uint8_t lamps = 0;
    CHECK(j1939_decode_dm1(d, 8, &lamps, dtc, 4) == 1);
    CHECK(dtc[0].spn == 110 && dtc[0].fmi == 0 && dtc[0].oc == 3);
    CHECK(lamps == 1);
}

/* REQ: FR-05 */
TEST(decodes_19_bit_spn_and_red_lamp) {
    /* SPN 520192 = 0x7F000 -> low 0x00, mid 0xF0, high 3 bits 0b111 in byte 5 with FMI 5 */
    const uint8_t d[6] = {0x10, 0xFF, 0x00, 0xF0, 0xE0 | 0x05, 0x01};
    j1939_dtc dtc[2];
    uint8_t lamps = 0;
    CHECK(j1939_decode_dm1(d, 6, &lamps, dtc, 2) == 1);
    CHECK(dtc[0].spn == 0x7F000u && dtc[0].fmi == 5 && dtc[0].oc == 1);
    CHECK(lamps == 2);
}

/* REQ: FR-05 */
TEST(dm1_no_active_dtc_pattern_returns_zero) {
    const uint8_t d[8] = {0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF};
    j1939_dtc dtc[2];
    CHECK(j1939_decode_dm1(d, 8, NULL, dtc, 2) == 0);
    j1939_session *s = j1939_session_new();
    j1939_event ev[4];
    CHECK(j1939_session_feed_line(s, "(1.0) trk02 18FECA00#00FF00000000FFFF", ev, 4) == 1);
    CHECK(ev[0].kind == J1939_EVT_DTC_NONE);
    j1939_session_free(s);
}

static const char *BAM_DM1[] = {
    /* BAM announce: 10 bytes, 2 packets, PGN 0xFECA */
    "(100.00) trk03 1CECFF00#200A0002FFCAFE00",
    "(100.05) trk03 1CEBFF00#0114006E0000006F",
    "(100.10) trk03 1CEBFF00#02000102FFFFFFFF",
};

/* REQ: FR-06 */
TEST(reassembles_bam_dm1_with_two_dtcs) {
    /* payload: lamps 0x14 (amber+red), flash 0x00, DTC1 SPN110 FMI0 OC0, DTC2 SPN111 FMI1 OC2 */
    j1939_session *s = j1939_session_new();
    j1939_event ev[8];
    CHECK(j1939_session_feed_line(s, BAM_DM1[0], ev, 8) == 0);
    CHECK(j1939_session_feed_line(s, BAM_DM1[1], ev, 8) == 0);
    int n = j1939_session_feed_line(s, BAM_DM1[2], ev, 8);
    CHECK(n == 2);
    CHECK(ev[0].kind == J1939_EVT_DTC && ev[0].spn == 110 && ev[0].fmi == 0);
    CHECK(ev[1].spn == 111 && ev[1].fmi == 1 && ev[1].oc == 2);
    CHECK(ev[0].lamp == 3);
    CHECK(strcmp(ev[1].vehicle, "trk03") == 0);
    j1939_stats st;
    j1939_session_stats(s, &st);
    CHECK(st.tp_messages == 1 && st.tp_errors == 0);
    j1939_session_free(s);
}

/* REQ: FR-06 */
TEST(bam_out_of_order_packet_is_rejected) {
    j1939_session *s = j1939_session_new();
    j1939_event ev[8];
    j1939_session_feed_line(s, BAM_DM1[0], ev, 8);
    CHECK(j1939_session_feed_line(s, BAM_DM1[2], ev, 8) == J1939_ERR_TP); /* seq 2 before 1 */
    CHECK(j1939_session_feed_line(s, BAM_DM1[1], ev, 8) == J1939_ERR_TP); /* session was dropped */
    j1939_stats st;
    j1939_session_stats(s, &st);
    CHECK(st.tp_errors == 2 && st.tp_messages == 0);
    j1939_session_free(s);
}

/* REQ: FR-06 */
TEST(bam_timeout_drops_message) {
    j1939_session *s = j1939_session_new();
    j1939_event ev[8];
    j1939_session_feed_line(s, BAM_DM1[0], ev, 8);
    j1939_session_feed_line(s, BAM_DM1[1], ev, 8);
    /* second packet arrives 1 s later: beyond the 750 ms T1 timeout */
    CHECK(j1939_session_feed_line(s, "(101.10) trk03 1CEBFF00#02000102FFFFFFFF", ev, 8) == J1939_ERR_TP);
    j1939_session_free(s);
}

/* REQ: FR-06 */
TEST(bam_sessions_are_independent_per_vehicle) {
    j1939_session *s = j1939_session_new();
    j1939_event ev[8];
    j1939_session_feed_line(s, BAM_DM1[0], ev, 8);
    j1939_session_feed_line(s, "(100.01) trk04 1CECFF00#200A0002FFCAFE00", ev, 8);
    j1939_session_feed_line(s, BAM_DM1[1], ev, 8);
    j1939_session_feed_line(s, "(100.06) trk04 1CEBFF00#0114006E0000006F", ev, 8);
    CHECK(j1939_session_feed_line(s, "(100.11) trk04 1CEBFF00#02000102FFFFFFFF", ev, 8) == 2);
    CHECK(strcmp(ev[0].vehicle, "trk04") == 0);
    CHECK(j1939_session_feed_line(s, BAM_DM1[2], ev, 8) == 2);
    CHECK(strcmp(ev[0].vehicle, "trk03") == 0);
    j1939_session_free(s);
}

/* REQ: FR-06 */
TEST(bam_announce_with_inconsistent_size_is_rejected) {
    j1939_session *s = j1939_session_new();
    j1939_event ev[8];
    /* 10 bytes need 2 packets, announce says 5 */
    CHECK(j1939_session_feed_line(s, "(1.0) trk03 1CECFF00#200A0005FFCAFE00", ev, 8) == J1939_ERR_TP);
    CHECK(j1939_session_feed_line(s, "(1.0) trk03 1CEBFF00#0114006E0000006F", ev, 8) == J1939_ERR_TP);
    j1939_session_free(s);
}

/* REQ: FR-01, FR-03 */
TEST(session_counts_lines_frames_and_errors) {
    j1939_session *s = j1939_session_new();
    j1939_event ev[8];
    j1939_session_feed_line(s, "(1.0) trk01 18FEEE00#7AFFFFFFFFFFFFFF", ev, 8);
    j1939_session_feed_line(s, "garbage", ev, 8);
    j1939_session_feed_line(s, "(2.0) trk01 18FEF200#BC020006FFFFFFFF", ev, 8);
    j1939_stats st;
    j1939_session_stats(s, &st);
    CHECK(st.lines == 3 && st.frames == 2 && st.parse_errors == 1 && st.events == 3);
    j1939_session_free(s);
}

/* REQ: FR-03 */
TEST(signal_names_are_stable) {
    CHECK(strcmp(j1939_signal_name(SIG_COOLANT_TEMP), "coolant_temp_c") == 0);
    CHECK(strcmp(j1939_signal_name(SIG_FUEL_ECONOMY), "fuel_economy_kmpl") == 0);
    CHECK(strcmp(j1939_signal_name(-1), "dtc") == 0);
    CHECK(sizeof(j1939_event) == 48); /* mirrored by the C# P/Invoke struct */
}

int main(void) {
    RUN(parses_valid_candump_line);
    RUN(accepts_short_frames_and_lowercase_hex);
    RUN(rejects_malformed_lines);
    RUN(extracts_pgn_source_and_priority);
    RUN(decodes_engine_speed_and_vehicle_speed);
    RUN(decodes_temperature_fuel_and_pressure);
    RUN(ignores_unknown_pgns_and_short_frames);
    RUN(skips_error_and_not_available_values);
    RUN(decodes_single_dm1_dtc_and_lamps);
    RUN(decodes_19_bit_spn_and_red_lamp);
    RUN(dm1_no_active_dtc_pattern_returns_zero);
    RUN(reassembles_bam_dm1_with_two_dtcs);
    RUN(bam_out_of_order_packet_is_rejected);
    RUN(bam_timeout_drops_message);
    RUN(bam_sessions_are_independent_per_vehicle);
    RUN(bam_announce_with_inconsistent_size_is_rejected);
    RUN(session_counts_lines_frames_and_errors);
    RUN(signal_names_are_stable);
    printf("%d tests, %d checks, %d failures\n", tests, checks, failures);
    return failures ? 1 : 0;
}
