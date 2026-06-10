/*
 * padbridge-flydigi: Flydigi Vader 5 Pro companion daemon.
 *
 * The Vader 5 Pro's extra buttons (C, Z, M1-M4, LM, RM, O) are invisible
 * to the kernel xpad driver: in XInput mode the controller only reports
 * them over a vendor HID interface, and only after being asked to stream
 * extended reports (the same channel the Flydigi Space app uses).
 *
 * This daemon finds that interface, enables the stream, and re-emits the
 * extra buttons as a small evdev device ("Flydigi Vader 5 Pro Paddles",
 * BTN_TRIGGER_HAPPY1..10) that PadBridge - or anything else - can read.
 * The controller's normal input (sticks, ABXY, triggers) is untouched;
 * it keeps flowing through the regular "Generic X-Box pad" device, and
 * since this daemon emits only buttons xpad never reports, nothing is
 * duplicated.
 *
 * Hotplug-aware: waits for the controller to appear, re-attaches after
 * disconnects, and removes the virtual device while the controller is
 * away.
 *
 * Protocol (as reverse-engineered by the flydigi-vader5 project,
 * github.com/BANANASJIM/flydigi-vader5):
 *   - vendor interface: USB iface 1, 32-byte unnumbered reports
 *   - init: write 5a a5 01 02 03 / 5a a5 a1 02 a3 / 5a a5 02 02 04 /
 *     5a a5 04 02 06 (zero-padded to 32), each acked by a 5a a5 reply
 *   - stream on: 5a a5 11 07 ff 01 ff ff ff 15 ("test mode")
 *   - extended report: starts 5a a5 ef; byte 13 = C(01) Z(02) M1(04)
 *     M2(08) M3(10) M4(20) LM(40) RM(80); byte 14 = O(01) Home(08)
 *
 * Build:
 *   gcc -O2 -o padbridge-flydigi padbridge-flydigi.c
 */

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <linux/input.h>
#include <linux/uinput.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define VENDOR_ID  0x37D7
#define PRODUCT_ID 0x2401
#define INTERFACE  1            /* HID_PHYS ends in .../input1 */

#define PKT_SIZE 32
#define VIRT_NAME "Flydigi Vader 5 Pro Paddles"

/* Extra-button bits -> emitted codes, in bit order. */
static const int EXT1_CODES[8] = {
    BTN_TRIGGER_HAPPY1,  /* C  */
    BTN_TRIGGER_HAPPY2,  /* Z  */
    BTN_TRIGGER_HAPPY3,  /* M1 */
    BTN_TRIGGER_HAPPY4,  /* M2 */
    BTN_TRIGGER_HAPPY5,  /* M3 */
    BTN_TRIGGER_HAPPY6,  /* M4 */
    BTN_TRIGGER_HAPPY7,  /* LM */
    BTN_TRIGGER_HAPPY8,  /* RM */
};
static const int EXT2_BITS[2]  = { 0x01 /* O */, 0x08 /* Home */ };
static const int EXT2_CODES[2] = { BTN_TRIGGER_HAPPY9, BTN_TRIGGER_HAPPY10 };

static volatile sig_atomic_t running = 1;
static void on_signal(int s) { (void)s; running = 0; }

/* Find /dev/hidrawN for the Vader 5 Pro's vendor interface. */
static int find_hidraw(char *path, size_t len) {
    DIR *dir = opendir("/sys/class/hidraw");
    if (!dir) return -1;

    struct dirent *e;
    int found = -1;
    while (found < 0 && (e = readdir(dir))) {
        if (strncmp(e->d_name, "hidraw", 6) != 0) continue;

        char uevent[PATH_MAX];
        snprintf(uevent, sizeof(uevent),
                 "/sys/class/hidraw/%s/device/uevent", e->d_name);
        FILE *f = fopen(uevent, "r");
        if (!f) continue;

        int id_ok = 0, iface_ok = 0;
        char line[256];
        while (fgets(line, sizeof(line), f)) {
            unsigned bus, vid, pid;
            if (sscanf(line, "HID_ID=%x:%x:%x", &bus, &vid, &pid) == 3)
                id_ok = (vid == VENDOR_ID && pid == PRODUCT_ID);
            char *phys = strstr(line, "HID_PHYS=");
            if (phys) {
                char *in = strrchr(phys, '/');
                int n;
                if (in && sscanf(in, "/input%d", &n) == 1)
                    iface_ok = (n == INTERFACE);
            }
        }
        fclose(f);

        if (id_ok && iface_ok) {
            snprintf(path, len, "/dev/%s", e->d_name);
            found = 0;
        }
    }
    closedir(dir);
    return found;
}

/* Write a zero-padded 32-byte command and wait briefly for a 5a a5 ack. */
static int send_cmd(int fd, const unsigned char *cmd, int cmd_len, int want_ack) {
    unsigned char pkt[PKT_SIZE] = {0};
    memcpy(pkt, cmd, cmd_len);
    if (write(fd, pkt, sizeof(pkt)) != sizeof(pkt)) return -1;
    if (!want_ack) return 0;

    for (int retry = 0; retry < 10; retry++) {
        struct pollfd pfd = { .fd = fd, .events = POLLIN };
        if (poll(&pfd, 1, 5) > 0) {
            unsigned char resp[PKT_SIZE];
            int n = (int)read(fd, resp, sizeof(resp));
            if (n >= 4 && resp[0] == 0x5a && resp[1] == 0xa5) return 0;
        }
    }
    return -1;
}

static int set_stream(int fd, int enable) {
    /* "test mode": checksum = sum of bytes 2..8 (0x15 on, 0x14 off) */
    unsigned char cmd[10] = { 0x5a, 0xa5, 0x11, 0x07, 0xff,
                              (unsigned char)(enable ? 0x01 : 0x00),
                              0xff, 0xff, 0xff,
                              (unsigned char)(enable ? 0x15 : 0x14) };
    return send_cmd(fd, cmd, sizeof(cmd), 0);
}

static int enable_stream(int fd) {
    static const unsigned char init_cmds[4][5] = {
        { 0x5a, 0xa5, 0x01, 0x02, 0x03 },
        { 0x5a, 0xa5, 0xa1, 0x02, 0xa3 },
        { 0x5a, 0xa5, 0x02, 0x02, 0x04 },
        { 0x5a, 0xa5, 0x04, 0x02, 0x06 },
    };

    /* Drain anything stale first. */
    unsigned char junk[PKT_SIZE];
    for (int i = 0; i < 10 && read(fd, junk, sizeof(junk)) > 0; i++) ;

    for (int i = 0; i < 4; i++)
        if (send_cmd(fd, init_cmds[i], 5, 1) < 0) return -1;
    return set_stream(fd, 1);
}

static int create_uinput(void) {
    int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
    if (fd < 0) { perror("open /dev/uinput"); return -1; }

    ioctl(fd, UI_SET_EVBIT, EV_KEY);
    ioctl(fd, UI_SET_EVBIT, EV_SYN);
    for (int i = 0; i < 8; i++) ioctl(fd, UI_SET_KEYBIT, EXT1_CODES[i]);
    for (int i = 0; i < 2; i++) ioctl(fd, UI_SET_KEYBIT, EXT2_CODES[i]);

    struct uinput_setup us = {0};
    us.id.bustype = BUS_VIRTUAL;
    us.id.vendor = VENDOR_ID;
    us.id.product = PRODUCT_ID;
    us.id.version = 1;
    strcpy(us.name, VIRT_NAME);
    ioctl(fd, UI_DEV_SETUP, &us);
    if (ioctl(fd, UI_DEV_CREATE) < 0) {
        perror("UI_DEV_CREATE");
        close(fd);
        return -1;
    }
    return fd;
}

static void emit(int fd, int type, int code, int val) {
    struct input_event ie = {0};
    ie.type = type; ie.code = code; ie.value = val;
    if (write(fd, &ie, sizeof(ie)) != sizeof(ie)) { /* gone; loop recovers */ }
}

static void emit_changes(int ufd, unsigned char prev, unsigned char curr,
                         const int *codes, const int *bits, int n) {
    int dirty = 0;
    for (int i = 0; i < n; i++) {
        int bit = bits ? bits[i] : (1 << i);
        int was = (prev & bit) != 0, is = (curr & bit) != 0;
        if (was != is) { emit(ufd, EV_KEY, codes[i], is); dirty = 1; }
    }
    if (dirty) emit(ufd, EV_SYN, SYN_REPORT, 0);
}

/* Read the stream until the controller goes away or we're stopped. */
static void run_session(int hid_fd, int ufd) {
    unsigned char prev1 = 0, prev2 = 0;

    while (running) {
        struct pollfd pfd = { .fd = hid_fd, .events = POLLIN };
        int r = poll(&pfd, 1, 500);
        if (r < 0 && errno != EINTR) return;
        if (r <= 0) continue;
        if (pfd.revents & (POLLERR | POLLHUP)) return;

        unsigned char pkt[PKT_SIZE];
        int n = (int)read(hid_fd, pkt, sizeof(pkt));
        if (n < 0) {
            if (errno == EAGAIN || errno == EINTR) continue;
            return;                      /* unplugged */
        }
        if (n < 17 || pkt[0] != 0x5a || pkt[1] != 0xa5 || pkt[2] != 0xef)
            continue;                    /* command reply or other traffic */

        emit_changes(ufd, prev1, pkt[13], EXT1_CODES, NULL, 8);
        emit_changes(ufd, prev2, pkt[14], EXT2_CODES, EXT2_BITS, 2);
        prev1 = pkt[13];
        prev2 = pkt[14];
    }
}

int main(void) {
    struct sigaction sa = {0};
    sa.sa_handler = on_signal;
    sigaction(SIGINT, &sa, NULL);
    sigaction(SIGTERM, &sa, NULL);

    printf("padbridge-flydigi started (waiting for Vader 5 Pro %04x:%04x)\n",
           VENDOR_ID, PRODUCT_ID);
    fflush(stdout);

    int announced_missing = 0;
    while (running) {
        char path[PATH_MAX];
        if (find_hidraw(path, sizeof(path)) < 0) {
            if (!announced_missing) {
                printf("controller not present; waiting\n");
                fflush(stdout);
                announced_missing = 1;
            }
            for (int i = 0; i < 20 && running; i++) usleep(100 * 1000);
            continue;
        }
        announced_missing = 0;

        int hid_fd = open(path, O_RDWR | O_NONBLOCK);
        if (hid_fd < 0) {
            fprintf(stderr, "open %s: %s (check hidraw permissions)\n",
                    path, strerror(errno));
            for (int i = 0; i < 20 && running; i++) usleep(100 * 1000);
            continue;
        }

        if (enable_stream(hid_fd) < 0) {
            fprintf(stderr, "%s: controller did not ack init; retrying\n", path);
            close(hid_fd);
            for (int i = 0; i < 20 && running; i++) usleep(100 * 1000);
            continue;
        }

        int ufd = create_uinput();
        if (ufd < 0) {
            set_stream(hid_fd, 0);
            close(hid_fd);
            return 1;                    /* uinput is a hard requirement */
        }

        printf("attached to %s; '%s' is up\n", path, VIRT_NAME);
        fflush(stdout);

        run_session(hid_fd, ufd);

        ioctl(ufd, UI_DEV_DESTROY);
        close(ufd);
        if (running) {
            printf("controller disconnected\n");
            fflush(stdout);
        } else {
            set_stream(hid_fd, 0);       /* leave the channel as we found it */
        }
        close(hid_fd);
    }

    printf("stopped.\n");
    return 0;
}
