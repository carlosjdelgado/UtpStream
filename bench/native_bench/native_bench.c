/*
 * native_bench — minimal libutp throughput benchmark.
 *
 * Single-process, two threads with one libutp context each (just like
 * UtpStream's internal model), in-memory data, no stdin/stdout. The aim is
 * a fair "what is the upper bound of libutp on this machine?" number.
 *
 * Tuning here mirrors UtpStream's defaults so the C and .NET numbers are
 * directly comparable:
 *   - opt_sndbuf / opt_rcvbuf = 64 MiB
 *   - poll/check_timeouts cadence ~1 ms
 *
 * Usage: native_bench <bytes>
 */

#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <pthread.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#include <errno.h>
#include <time.h>
#include <poll.h>
#include <fcntl.h>

#include "utp.h"

#define LARGE_BUF (4 * 1024 * 1024)
#define DGRAM_BUF 65536
#define TICK_MS    1

typedef struct {
    int fd;
    utp_context *ctx;
    utp_socket *sock;
    int connected;
    int writable;
    int peer_eof;
    int destroyed;

    /* sender state */
    const uint8_t *send_data;
    size_t send_total;
    size_t send_offset;

    /* receiver state */
    size_t recv_total;
    size_t recv_skip;   /* start timing after this many bytes received */
    size_t recv_target;
    int finished;

    pthread_mutex_t mu;
} endpoint_t;

static double now_sec(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec + ts.tv_nsec / 1e9;
}

/* Fraction of bytes to skip at the start before beginning timing.
   Both benchmarks start the clock from the receiver's perspective once
   this threshold is crossed, discarding the LEDBAT slow-start phase and
   measuring only steady-state throughput. */
#define SKIP_FRAC 0.20

/* Timing window: started by the receiver once SKIP_FRAC of the bytes have
   arrived (steady-state begins), stopped when the full target is reached.
   Written once each from the receiver thread, read by main after join —
   no atomics needed. */
static double g_t_start = 0;
static double g_t_end   = 0;

/* ---- callbacks (shared) ---- */

static unsigned long long cb_sendto(utp_callback_arguments *a) {
    endpoint_t *ep = (endpoint_t *)utp_context_get_userdata(a->context);
    sendto(ep->fd, a->buf, a->len, 0, a->address, a->address_len);
    return 0;
}

static unsigned long long cb_on_error(utp_callback_arguments *a) {
    endpoint_t *ep = (endpoint_t *)utp_context_get_userdata(a->context);
    fprintf(stderr, "utp error: %s\n", utp_error_code_names[a->error_code]);
    ep->finished = 1;
    return 0;
}

static unsigned long long cb_log(utp_callback_arguments *a) {
    (void)a;
    return 0;
}

/* ---- sender callbacks ---- */

static void sender_pump_writes(endpoint_t *ep) {
    while (ep->send_offset < ep->send_total) {
        size_t left = ep->send_total - ep->send_offset;
        ssize_t w = utp_write(ep->sock,
                              (void *)(ep->send_data + ep->send_offset),
                              left);
        if (w <= 0) return;
        ep->send_offset += (size_t)w;
    }
    /* Don't issue a shutdown — the receiver knows how many bytes to expect
       and will close from its side once the count is reached. The sender
       sees UTP_STATE_DESTROYING when libutp tears down. */
}

static unsigned long long sender_cb_state_change(utp_callback_arguments *a) {
    endpoint_t *ep = (endpoint_t *)utp_context_get_userdata(a->context);
    switch (a->state) {
        case UTP_STATE_CONNECT:
            ep->connected = 1;
            ep->writable = 1;
            sender_pump_writes(ep);
            break;
        case UTP_STATE_WRITABLE:
            ep->writable = 1;
            sender_pump_writes(ep);
            break;
        case UTP_STATE_EOF:
            /* Peer closed — for our purposes, they've drained everything,
               we're done. (UTP_STATE_DESTROYING may never arrive if the
               peer already tore down its socket and our FIN is unacked.) */
            ep->peer_eof = 1;
            ep->finished = 1;
            if (ep->sock) utp_close(ep->sock);
            break;
        case UTP_STATE_DESTROYING:
            ep->destroyed = 1;
            ep->finished = 1;
            break;
    }
    return 0;
}

/* ---- receiver callbacks ---- */

static unsigned long long receiver_cb_firewall(utp_callback_arguments *a) {
    (void)a;
    return 0; /* accept */
}

static unsigned long long receiver_cb_accept(utp_callback_arguments *a) {
    endpoint_t *ep = (endpoint_t *)utp_context_get_userdata(a->context);
    ep->sock = a->socket;
    ep->connected = 1;
    return 0;
}

/* User-side buffer the receiver "reads into". Same shape as
   UtpStreamBench's 64 KiB buf — mirrors what a real reader does so we
   don't give the C side an unfair zero-copy advantage. */
static unsigned char g_recv_sink[64 * 1024];

static unsigned long long receiver_cb_on_read(utp_callback_arguments *a) {
    endpoint_t *ep = (endpoint_t *)utp_context_get_userdata(a->context);

    /* Copy the incoming chunk to the user-side sink, exactly mirroring
       the work UtpStreamBench's await ReadAsync(buf) does. */
    size_t left = a->len;
    const unsigned char *src = a->buf;
    while (left > 0) {
        size_t n = left < sizeof(g_recv_sink) ? left : sizeof(g_recv_sink);
        memcpy(g_recv_sink, src, n);
        src  += n;
        left -= n;
    }

    ep->recv_total += a->len;
    /* Start the steady-state timer once the skip threshold is crossed. */
    if (g_t_start == 0 && ep->recv_total >= ep->recv_skip)
        g_t_start = now_sec();
    utp_read_drained(a->socket);
    if (ep->recv_total >= ep->recv_target && ep->sock && !ep->finished) {
        if (g_t_end == 0) g_t_end = now_sec();
        utp_close(ep->sock);
    }
    return 0;
}

static unsigned long long receiver_cb_state_change(utp_callback_arguments *a) {
    endpoint_t *ep = (endpoint_t *)utp_context_get_userdata(a->context);
    switch (a->state) {
        case UTP_STATE_EOF:
            ep->peer_eof = 1;
            if (ep->sock) utp_close(ep->sock);
            break;
        case UTP_STATE_DESTROYING:
            ep->destroyed = 1;
            ep->finished = 1;
            break;
    }
    return 0;
}

static unsigned long long cb_get_read_buffer_size(utp_callback_arguments *a) {
    (void)a;
    return 0; /* always advertise full window */
}

/* ---- pump loop (per endpoint) ---- */

static void pump(endpoint_t *ep) {
    uint8_t buf[DGRAM_BUF];
    struct sockaddr_in peer;
    socklen_t plen;
    struct pollfd pfd = { .fd = ep->fd, .events = POLLIN };

    while (!ep->finished) {
        int r = poll(&pfd, 1, TICK_MS);
        if (r > 0 && (pfd.revents & POLLIN)) {
            for (;;) {
                plen = sizeof(peer);
                ssize_t n = recvfrom(ep->fd, buf, sizeof(buf), MSG_DONTWAIT,
                                     (struct sockaddr *)&peer, &plen);
                if (n <= 0) break;
                utp_process_udp(ep->ctx, buf, n,
                                (struct sockaddr *)&peer, plen);
            }
            utp_issue_deferred_acks(ep->ctx);
        }
        utp_check_timeouts(ep->ctx);
    }
}

/* ---- setup helpers ---- */

static int setup_udp(uint16_t port_hbo) {
    int fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (fd < 0) { perror("socket"); exit(1); }
    int sndbuf = LARGE_BUF, rcvbuf = LARGE_BUF;
    setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &sndbuf, sizeof(sndbuf));
    setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &rcvbuf, sizeof(rcvbuf));
    struct sockaddr_in a = {0};
    a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    a.sin_port = htons(port_hbo);
    if (bind(fd, (struct sockaddr *)&a, sizeof(a)) < 0) { perror("bind"); exit(1); }
    return fd;
}

static utp_context *setup_ctx(endpoint_t *ep, int as_listener) {
    utp_context *ctx = utp_init(2);
    utp_context_set_userdata(ctx, ep);
    utp_context_set_option(ctx, UTP_SNDBUF, LARGE_BUF);
    utp_context_set_option(ctx, UTP_RCVBUF, LARGE_BUF);

    utp_set_callback(ctx, UTP_SENDTO,                cb_sendto);
    utp_set_callback(ctx, UTP_ON_ERROR,              cb_on_error);
    utp_set_callback(ctx, UTP_LOG,                   cb_log);
    utp_set_callback(ctx, UTP_GET_READ_BUFFER_SIZE,  cb_get_read_buffer_size);

    if (as_listener) {
        utp_set_callback(ctx, UTP_ON_FIREWALL,     receiver_cb_firewall);
        utp_set_callback(ctx, UTP_ON_ACCEPT,       receiver_cb_accept);
        utp_set_callback(ctx, UTP_ON_READ,         receiver_cb_on_read);
        utp_set_callback(ctx, UTP_ON_STATE_CHANGE, receiver_cb_state_change);
    } else {
        utp_set_callback(ctx, UTP_ON_STATE_CHANGE, sender_cb_state_change);
    }
    return ctx;
}

/* ---- thread entry points ---- */

static void *receiver_main(void *arg) {
    endpoint_t *ep = (endpoint_t *)arg;
    pump(ep);
    return NULL;
}

static void *sender_main(void *arg) {
    endpoint_t *ep = (endpoint_t *)arg;
    pump(ep);
    return NULL;
}

/* ---- main ---- */

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: %s <bytes>\n", argv[0]);
        return 2;
    }
    size_t total = (size_t)strtoull(argv[1], NULL, 10);

    /* In-memory payload (zeros — content doesn't matter for throughput). */
    uint8_t *payload = (uint8_t *)calloc(1, total);
    if (!payload) { perror("calloc"); return 1; }

    /* Receiver endpoint: bind ephemeral, listen for incoming. */
    endpoint_t recv_ep = {0};
    pthread_mutex_init(&recv_ep.mu, NULL);
    recv_ep.recv_skip   = (size_t)((double)total * SKIP_FRAC);
    recv_ep.recv_target = total;
    recv_ep.fd = setup_udp(0); /* ephemeral */
    recv_ep.ctx = setup_ctx(&recv_ep, /*listener=*/1);

    struct sockaddr_in recv_local;
    socklen_t recv_local_len = sizeof(recv_local);
    getsockname(recv_ep.fd, (struct sockaddr *)&recv_local, &recv_local_len);
    uint16_t recv_port_hbo = ntohs(recv_local.sin_port);

    /* Sender endpoint: bind ephemeral, connect to receiver. */
    endpoint_t send_ep = {0};
    pthread_mutex_init(&send_ep.mu, NULL);
    send_ep.send_data = payload;
    send_ep.send_total = total;
    send_ep.fd = setup_udp(0);
    send_ep.ctx = setup_ctx(&send_ep, /*listener=*/0);
    send_ep.sock = utp_create_socket(send_ep.ctx);

    /* Trigger the connect BEFORE spawning the sender pump — otherwise
       utp_connect (called from main) would race with the pump's calls
       into the same ctx. After utp_connect the SYN is in the kernel
       send queue; once the pump starts it will receive the SYN-ACK and
       fire UTP_STATE_CONNECT, which kicks off sender_pump_writes. */
    struct sockaddr_in dest = {0};
    dest.sin_family = AF_INET;
    dest.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    dest.sin_port = htons(recv_port_hbo);

    utp_connect(send_ep.sock, (struct sockaddr *)&dest, sizeof(dest));

    pthread_t recv_th, send_th;
    pthread_create(&recv_th, NULL, receiver_main, &recv_ep);
    pthread_create(&send_th, NULL, sender_main, &send_ep);

    /* Wait for completion. Receiver finishes on EOF; sender finishes when
       its socket is destroyed (post-shutdown). */
    pthread_join(send_th, NULL);
    pthread_join(recv_th, NULL);

    /* Report only the steady-state window: bytes received after the skip
       threshold, timed from the same receiver-side perspective as the
       managed benchmark. */
    size_t steady_bytes = recv_ep.recv_total - recv_ep.recv_skip;
    double secs = g_t_end - g_t_start;
    double mibps = (double)steady_bytes / 1048576.0 / secs;
    printf("native: received %zu bytes in %.2f s (%.2f MiB/s)\n",
           steady_bytes, secs, mibps);

    free(payload);
    return recv_ep.recv_total == total ? 0 : 1;
}
