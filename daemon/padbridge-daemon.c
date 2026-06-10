/*
 * padbridge-daemon: generic controller-to-keyboard/button bridge.
 *
 * Reads EV_KEY events from a configured input device and re-emits them
 * as different key/button codes through uinput virtual devices.
 *
 * Two operating modes:
 *
 *   grab = false (default)
 *     The source device is untouched; mapped buttons are emitted as
 *     extra events from "PadBridge Virtual Keyboard". Ideal for extra
 *     buttons games can't see (e.g. Vader 5 Pro back buttons -> F-keys).
 *     Button-to-button mappings are NOT useful here: games won't merge
 *     a second virtual gamepad with the real one.
 *
 *   grab = true (exclusive)
 *     The source device is grabbed (EVIOCGRAB) and fully forwarded
 *     through a clone device "<name> (PadBridge)" with the same
 *     identity, axes and buttons; mapped buttons are rewritten in
 *     transit (keyboard targets go to the virtual keyboard instead).
 *     This makes button-to-button remaps real, at the cost of
 *     force-feedback passthrough.
 *
 * Config: ~/.config/padbridge/padbridge.conf (or argv[1]), format:
 *
 *   device = Vader 5 Pro Virtual Gamepad
 *   grab = false
 *   map BTN_TRIGGER_HAPPY1 = KEY_I
 *
 * The config is watched with inotify and reloaded automatically.
 *
 * Build:
 *   gcc -O2 -o padbridge-daemon padbridge-daemon.c
 */

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
#include <sys/inotify.h>
#include <unistd.h>

#include "event-names.h"

#define VIRT_KBD_NAME "PadBridge Virtual Keyboard"
#define MAX_MAPPINGS 128

/* Targets below the button range are keyboard keys. */
#define KEYBOARD_TARGET(code) ((code) < BTN_MISC)

struct mapping { int src; int dst; };

static volatile sig_atomic_t running = 1;

static char config_path[PATH_MAX];
static char device_name[256];
static struct mapping mappings[MAX_MAPPINGS];
static int n_mappings = 0;
static int grab_mode = 0;

static int kbd_fd = -1;   /* virtual keyboard for KEY_* targets        */
static int pad_fd = -1;   /* clone of the source device (grab mode)    */

static void on_signal(int s) { (void)s; running = 0; }

static int code_from_name(const char *name) {
    for (int i = 0; i < EV_CODE_NAMES_LEN; i++)
        if (strcmp(EV_CODE_NAMES[i].name, name) == 0)
            return EV_CODE_NAMES[i].code;
    return -1;
}

static const char *name_from_code(int code) {
    for (int i = 0; i < EV_CANONICAL_NAMES_LEN; i++)
        if (EV_CANONICAL_NAMES[i].code == code)
            return EV_CANONICAL_NAMES[i].name;
    return "?";
}

static char *trim(char *s) {
    while (*s == ' ' || *s == '\t') s++;
    char *end = s + strlen(s);
    while (end > s && (end[-1] == ' ' || end[-1] == '\t' ||
                       end[-1] == '\n' || end[-1] == '\r'))
        *--end = 0;
    return s;
}

static int parse_bool(const char *s) {
    return strcmp(s, "true") == 0 || strcmp(s, "on") == 0 ||
           strcmp(s, "yes") == 0 || strcmp(s, "1") == 0;
}

static int load_config(void) {
    FILE *f = fopen(config_path, "r");
    if (!f) {
        fprintf(stderr, "cannot open config %s: %s\n", config_path, strerror(errno));
        return -1;
    }

    device_name[0] = 0;
    n_mappings = 0;
    grab_mode = 0;

    char line[512];
    int lineno = 0;
    while (fgets(line, sizeof(line), f)) {
        lineno++;
        char *s = trim(line);
        if (!*s || *s == '#') continue;

        char *eq = strchr(s, '=');
        if (!eq) { fprintf(stderr, "config line %d: no '='\n", lineno); continue; }
        *eq = 0;
        char *key = trim(s);
        char *val = trim(eq + 1);

        if (strcmp(key, "device") == 0) {
            snprintf(device_name, sizeof(device_name), "%s", val);
        } else if (strcmp(key, "grab") == 0) {
            grab_mode = parse_bool(val);
        } else if (strncmp(key, "map ", 4) == 0 || strncmp(key, "map\t", 4) == 0) {
            char *src_name = trim(key + 4);
            int src = code_from_name(src_name);
            int dst = code_from_name(val);
            if (src < 0 || dst < 0) {
                fprintf(stderr, "config line %d: unknown code '%s' or '%s'\n",
                        lineno, src_name, val);
                continue;
            }
            int found = 0;
            for (int i = 0; i < n_mappings; i++)
                if (mappings[i].src == src) { mappings[i].dst = dst; found = 1; break; }
            if (!found && n_mappings < MAX_MAPPINGS)
                mappings[n_mappings++] = (struct mapping){ src, dst };
        } else {
            fprintf(stderr, "config line %d: unknown key '%s'\n", lineno, key);
        }
    }
    fclose(f);

    printf("config loaded: device='%s', grab=%s, %d mapping(s)\n",
           device_name, grab_mode ? "true" : "false", n_mappings);
    for (int i = 0; i < n_mappings; i++)
        printf("  %s -> %s\n", name_from_code(mappings[i].src),
               name_from_code(mappings[i].dst));
    fflush(stdout);
    return 0;
}

static void destroy_dev(int *fd) {
    if (*fd >= 0) {
        ioctl(*fd, UI_DEV_DESTROY);
        close(*fd);
        *fd = -1;
    }
}

static void emit(int fd, int type, int code, int val) {
    struct input_event ie = {0};
    ie.type = type; ie.code = code; ie.value = val;
    if (write(fd, &ie, sizeof(ie)) != sizeof(ie)) { /* device gone; rebuilt on reload */ }
}

/* Virtual keyboard carrying the KEY_* targets (all targets in non-grab mode). */
static int create_kbd(void) {
    destroy_dev(&kbd_fd);

    int wanted = 0;
    for (int i = 0; i < n_mappings; i++)
        if (!grab_mode || KEYBOARD_TARGET(mappings[i].dst)) wanted++;
    if (!wanted) return 0;

    kbd_fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
    if (kbd_fd < 0) { perror("open /dev/uinput"); return -1; }

    ioctl(kbd_fd, UI_SET_EVBIT, EV_KEY);
    ioctl(kbd_fd, UI_SET_EVBIT, EV_SYN);
    for (int i = 0; i < n_mappings; i++)
        if (!grab_mode || KEYBOARD_TARGET(mappings[i].dst))
            ioctl(kbd_fd, UI_SET_KEYBIT, mappings[i].dst);

    struct uinput_setup us = {0};
    us.id.bustype = BUS_VIRTUAL;
    us.id.version = 1;
    strcpy(us.name, VIRT_KBD_NAME);
    ioctl(kbd_fd, UI_DEV_SETUP, &us);
    if (ioctl(kbd_fd, UI_DEV_CREATE) < 0) {
        perror("UI_DEV_CREATE (keyboard)");
        close(kbd_fd);
        kbd_fd = -1;
        return -1;
    }
    return 0;
}

static int bit_set(const unsigned char *bits, int code) {
    return bits[code / 8] & (1 << (code % 8));
}

/* Grab mode: clone the source device's identity and capabilities so games
 * see one gamepad with remapped buttons instead of the original. */
static int create_pad_clone(int src) {
    destroy_dev(&pad_fd);

    pad_fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
    if (pad_fd < 0) { perror("open /dev/uinput"); return -1; }

    unsigned char evb[(EV_MAX + 7) / 8] = {0};
    ioctl(src, EVIOCGBIT(0, sizeof(evb)), evb);

    ioctl(pad_fd, UI_SET_EVBIT, EV_SYN);
    ioctl(pad_fd, UI_SET_EVBIT, EV_KEY);

    unsigned char keyb[(KEY_MAX + 7) / 8] = {0};
    ioctl(src, EVIOCGBIT(EV_KEY, sizeof(keyb)), keyb);
    for (int c = 0; c <= KEY_MAX; c++)
        if (bit_set(keyb, c))
            ioctl(pad_fd, UI_SET_KEYBIT, c);
    for (int i = 0; i < n_mappings; i++)
        if (!KEYBOARD_TARGET(mappings[i].dst))
            ioctl(pad_fd, UI_SET_KEYBIT, mappings[i].dst);

    if (bit_set(evb, EV_ABS)) {
        ioctl(pad_fd, UI_SET_EVBIT, EV_ABS);
        unsigned char absb[(ABS_MAX + 7) / 8] = {0};
        ioctl(src, EVIOCGBIT(EV_ABS, sizeof(absb)), absb);
        for (int c = 0; c <= ABS_MAX; c++) {
            if (!bit_set(absb, c)) continue;
            struct uinput_abs_setup uas = {0};
            uas.code = c;
            ioctl(src, EVIOCGABS(c), &uas.absinfo);
            ioctl(pad_fd, UI_ABS_SETUP, &uas);
        }
    }

    if (bit_set(evb, EV_REL)) {
        ioctl(pad_fd, UI_SET_EVBIT, EV_REL);
        unsigned char relb[(REL_MAX + 7) / 8] = {0};
        ioctl(src, EVIOCGBIT(EV_REL, sizeof(relb)), relb);
        for (int c = 0; c <= REL_MAX; c++)
            if (bit_set(relb, c))
                ioctl(pad_fd, UI_SET_RELBIT, c);
    }

    if (bit_set(evb, EV_MSC)) {
        ioctl(pad_fd, UI_SET_EVBIT, EV_MSC);
        unsigned char mscb[(MSC_MAX + 7) / 8] = {0};
        ioctl(src, EVIOCGBIT(EV_MSC, sizeof(mscb)), mscb);
        for (int c = 0; c <= MSC_MAX; c++)
            if (bit_set(mscb, c))
                ioctl(pad_fd, UI_SET_MSCBIT, c);
    }

    /* Same bus/vendor/product/version => SDL & friends apply the same
     * controller profile to the clone as to the original. */
    struct uinput_setup us = {0};
    ioctl(src, EVIOCGID, &us.id);
    snprintf(us.name, sizeof(us.name), "%.*s (PadBridge)",
             (int)(sizeof(us.name) - sizeof(" (PadBridge)")), device_name);
    ioctl(pad_fd, UI_DEV_SETUP, &us);
    if (ioctl(pad_fd, UI_DEV_CREATE) < 0) {
        perror("UI_DEV_CREATE (pad clone)");
        close(pad_fd);
        pad_fd = -1;
        return -1;
    }
    printf("created clone '%s'\n", us.name);
    fflush(stdout);
    return 0;
}

static int open_source(void) {
    if (!device_name[0]) return -1;
    char path[64], name[256];
    for (int i = 0; i < 512; i++) {
        snprintf(path, sizeof(path), "/dev/input/event%d", i);
        int fd = open(path, O_RDONLY);
        if (fd < 0) continue;
        name[0] = 0;
        ioctl(fd, EVIOCGNAME(sizeof(name)), name);
        if (strcmp(name, device_name) == 0) {
            if (grab_mode) {
                /* Grab first: if another process (e.g. Steam Input) owns
                 * the device this fails cleanly without clone churn. */
                if (ioctl(fd, EVIOCGRAB, (void *)1) < 0) {
                    fprintf(stderr, "grab of %s (%s) failed: %s "
                            "(is Steam Input or another remapper using it?)\n",
                            path, name, strerror(errno));
                    close(fd);
                    return -1;
                }
                if (create_pad_clone(fd) < 0) {
                    ioctl(fd, EVIOCGRAB, (void *)0);
                    close(fd);
                    return -1;
                }
            }
            printf("source connected: %s (%s)%s\n", name, path,
                   grab_mode ? " [grabbed]" : "");
            fflush(stdout);
            return fd;
        }
        close(fd);
    }
    return -1;
}

static void handle_event(const struct input_event *ev) {
    if (ev->type == EV_KEY) {
        for (int i = 0; i < n_mappings; i++) {
            if (mappings[i].src != ev->code) continue;
            int dst = mappings[i].dst;
            if (!grab_mode || KEYBOARD_TARGET(dst)) {
                if (kbd_fd >= 0) {
                    emit(kbd_fd, EV_KEY, dst, ev->value);
                    emit(kbd_fd, EV_SYN, SYN_REPORT, 0);
                }
            } else if (pad_fd >= 0) {
                emit(pad_fd, EV_KEY, dst, ev->value);
            }
            return;
        }
        /* Unmapped key: in grab mode we own the device, so forward it. */
        if (grab_mode && pad_fd >= 0)
            emit(pad_fd, EV_KEY, ev->code, ev->value);
    } else if (grab_mode && pad_fd >= 0) {
        /* Axes, sync, scancodes... pass through to the clone. */
        emit(pad_fd, ev->type, ev->code, ev->value);
    }
}

int main(int argc, char **argv) {
    signal(SIGINT, on_signal);
    signal(SIGTERM, on_signal);

    if (argc > 1) {
        snprintf(config_path, sizeof(config_path), "%s", argv[1]);
    } else {
        const char *base = getenv("XDG_CONFIG_HOME");
        if (base && *base)
            snprintf(config_path, sizeof(config_path), "%s/padbridge/padbridge.conf", base);
        else
            snprintf(config_path, sizeof(config_path), "%s/.config/padbridge/padbridge.conf",
                     getenv("HOME") ? getenv("HOME") : ".");
    }

    /* Watch the config's directory: editors and the GUI replace the file
     * by rename, which would invalidate a watch on the file itself. */
    char config_dir[PATH_MAX], config_file[256];
    snprintf(config_dir, sizeof(config_dir), "%s", config_path);
    char *slash = strrchr(config_dir, '/');
    if (slash) {
        snprintf(config_file, sizeof(config_file), "%s", slash + 1);
        *slash = 0;
    } else {
        snprintf(config_file, sizeof(config_file), "%s", config_dir);
        strcpy(config_dir, ".");
    }

    int ino_fd = inotify_init1(IN_NONBLOCK);
    if (ino_fd >= 0 &&
        inotify_add_watch(ino_fd, config_dir,
                          IN_CLOSE_WRITE | IN_MOVED_TO | IN_CREATE) < 0)
        fprintf(stderr, "inotify watch on %s failed: %s\n", config_dir, strerror(errno));

    load_config();
    create_kbd();

    printf("padbridge-daemon started (config: %s)\n", config_path);
    fflush(stdout);

    int src = -1;
    int rescan_wait = 0;

    while (running) {
        if (src < 0 && device_name[0]) {
            if (rescan_wait <= 0) {
                src = open_source();
                rescan_wait = 1; /* seconds between scans while disconnected */
            }
        }

        struct pollfd pfds[2];
        int n = 0;
        int src_idx = -1, ino_idx = -1;
        if (src >= 0)    { pfds[n].fd = src;    pfds[n].events = POLLIN; src_idx = n++; }
        if (ino_fd >= 0) { pfds[n].fd = ino_fd; pfds[n].events = POLLIN; ino_idx = n++; }

        int r = poll(pfds, n, 1000);
        if (r < 0) {
            if (errno == EINTR) continue;
            break;
        }
        if (r == 0) { rescan_wait--; continue; }

        if (ino_idx >= 0 && (pfds[ino_idx].revents & POLLIN)) {
            char buf[4096];
            ssize_t len = read(ino_fd, buf, sizeof(buf));
            int relevant = 0;
            for (ssize_t off = 0; off < len; ) {
                struct inotify_event *ev = (struct inotify_event *)(buf + off);
                if (ev->len && strcmp(ev->name, config_file) == 0) relevant = 1;
                off += sizeof(struct inotify_event) + ev->len;
            }
            if (relevant) {
                printf("config changed, reloading\n");
                /* Tear everything down: grab state, clone capabilities and
                 * keyboard keybits may all change with the new config. */
                if (src >= 0) { close(src); src = -1; }
                destroy_dev(&pad_fd);
                load_config();
                create_kbd();
                rescan_wait = 0;
            }
        }

        if (src_idx >= 0 && (pfds[src_idx].revents & (POLLIN | POLLERR | POLLHUP))) {
            struct input_event ev;
            if (read(src, &ev, sizeof(ev)) != sizeof(ev)) {
                printf("source disconnected, waiting...\n");
                fflush(stdout);
                close(src);
                src = -1;
                destroy_dev(&pad_fd);
                rescan_wait = 0;
                continue;
            }
            handle_event(&ev);
        }
    }

    if (src >= 0) close(src);
    if (ino_fd >= 0) close(ino_fd);
    destroy_dev(&pad_fd);
    destroy_dev(&kbd_fd);
    printf("stopped.\n");
    return 0;
}
