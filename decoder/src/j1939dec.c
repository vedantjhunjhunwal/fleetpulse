/* j1939dec - decode candump J1939 logs to CSV or JSON Lines.
 *
 *   j1939dec [--format csv|jsonl] [--stats] [--bench] FILE...   (or stdin)
 *
 * --bench loads the inputs into memory, decodes them without writing output
 * and reports throughput in frames per second.
 */
#define _POSIX_C_SOURCE 200809L
#include "j1939.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define MAX_EVENTS 128
/* Exit status: 0 on success (malformed lines are skipped and counted), 2 on I/O errors. */

static void print_event(const j1939_event *e, int jsonl) {
    if (jsonl) {
        if (e->kind == J1939_EVT_SIGNAL)
            printf("{\"ts\":%.3f,\"vehicle\":\"%s\",\"kind\":\"signal\",\"signal\":\"%s\",\"spn\":%u,\"value\":%.4f}\n",
                   e->ts, e->vehicle, j1939_signal_name(e->signal), e->spn, e->value);
        else
            printf("{\"ts\":%.3f,\"vehicle\":\"%s\",\"kind\":\"%s\",\"spn\":%u,\"fmi\":%u,\"oc\":%u,\"lamp\":%u}\n",
                   e->ts, e->vehicle, e->kind == J1939_EVT_DTC ? "dtc" : "dtc_none", e->spn, e->fmi, e->oc, e->lamp);
    } else {
        if (e->kind == J1939_EVT_SIGNAL)
            printf("%.3f,%s,signal,%s,%u,%.4f,,,\n", e->ts, e->vehicle, j1939_signal_name(e->signal), e->spn, e->value);
        else
            printf("%.3f,%s,%s,,%u,,%u,%u,%u\n", e->ts, e->vehicle, e->kind == J1939_EVT_DTC ? "dtc" : "dtc_none",
                   e->spn, e->fmi, e->oc, e->lamp);
    }
}

static char *slurp(FILE *fp, size_t *len) {
    size_t cap = 1 << 20, n = 0;
    char *buf = malloc(cap);
    if (!buf) return NULL;
    size_t r;
    while ((r = fread(buf + n, 1, cap - n - 1, fp)) > 0) {
        n += r;
        if (cap - n - 1 == 0) {
            char *nb = realloc(buf, cap *= 2);
            if (!nb) {
                free(buf);
                return NULL;
            }
            buf = nb;
        }
    }
    buf[n] = '\0';
    *len = n;
    return buf;
}

static double now_s(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec + (double)t.tv_nsec / 1e9;
}

int main(int argc, char **argv) {
    int jsonl = 0, stats = 0, bench = 0, nfiles = 0;
    const char **files = calloc((size_t)argc, sizeof(char *));
    if (!files) return 2;
    for (int i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--format") && i + 1 < argc) {
            jsonl = !strcmp(argv[++i], "jsonl");
        } else if (!strcmp(argv[i], "--stats")) {
            stats = 1;
        } else if (!strcmp(argv[i], "--bench")) {
            bench = 1;
        } else if (!strcmp(argv[i], "--version")) {
            printf("j1939dec %s\n", j1939_version());
            return 0;
        } else if (!strcmp(argv[i], "-h") || !strcmp(argv[i], "--help")) {
            fprintf(stderr, "usage: j1939dec [--format csv|jsonl] [--stats] [--bench] [FILE...]\n");
            return 0;
        } else {
            files[nfiles++] = argv[i];
        }
    }

    /* load all inputs */
    size_t total = 0;
    char **bufs = calloc((size_t)(nfiles ? nfiles : 1), sizeof(char *));
    size_t *lens = calloc((size_t)(nfiles ? nfiles : 1), sizeof(size_t));
    if (!bufs || !lens) return 2;
    int nbuf = nfiles ? nfiles : 1;
    for (int i = 0; i < nbuf; i++) {
        FILE *fp = nfiles ? fopen(files[i], "rb") : stdin;
        if (!fp) {
            perror(files[i]);
            return 2;
        }
        bufs[i] = slurp(fp, &lens[i]);
        if (nfiles) fclose(fp);
        if (!bufs[i]) return 2;
        total += lens[i];
    }

    j1939_session *s = j1939_session_new();
    j1939_event ev[MAX_EVENTS];
    if (!bench && !jsonl) printf("ts,vehicle,kind,signal,spn,value,fmi,oc,lamp\n");
    double t0 = now_s();
    for (int i = 0; i < nbuf; i++) {
        char *p = bufs[i];
        while (*p) {
            char *nl = strchr(p, '\n');
            if (nl) *nl = '\0';
            if (*p) {
                int n = j1939_session_feed_line(s, p, ev, MAX_EVENTS);
                if (!bench)
                    for (int k = 0; k < n; k++) print_event(&ev[k], jsonl);
            }
            if (!nl) break;
            *nl = '\n';
            p = nl + 1;
        }
    }
    double dt = now_s() - t0;

    j1939_stats st;
    j1939_session_stats(s, &st);
    if (stats || bench) {
        fprintf(stderr,
                "lines=%llu frames=%llu events=%llu bam_messages=%llu parse_errors=%llu tp_errors=%llu bytes=%zu\n",
                (unsigned long long)st.lines, (unsigned long long)st.frames, (unsigned long long)st.events,
                (unsigned long long)st.tp_messages, (unsigned long long)st.parse_errors,
                (unsigned long long)st.tp_errors, total);
    }
    if (bench) {
        fprintf(stderr, "decoded %llu frames in %.3f s = %.2f M frames/s (single thread, output disabled)\n",
                (unsigned long long)st.frames, dt, (double)st.frames / dt / 1e6);
    }
    j1939_session_free(s);
    for (int i = 0; i < nbuf; i++) free(bufs[i]);
    free(bufs);
    free(lens);
    free(files);
    return 0; /* malformed lines are counted in --stats, not fatal */
}
