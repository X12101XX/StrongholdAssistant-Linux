#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>
#include <wayland-client.h>

#include "wlr-screencopy-unstable-v1-client-protocol.h"
#include "wlr-virtual-pointer-unstable-v1-client-protocol.h"
#include "virtual-keyboard-unstable-v1-client-protocol.h"

#define MAX_OUTPUTS 16

typedef struct {
    uint32_t global_name;
    struct wl_output *output;
    char name[64];
    int32_t scale;
    int32_t mode_w;
    int32_t mode_h;
} output_entry_t;

typedef struct {
    pthread_mutex_t lock;
    bool initialized;
    struct wl_display *display;
    struct wl_registry *registry;
    struct wl_shm *shm;
    struct wl_seat *seat;
    struct zwlr_screencopy_manager_v1 *screencopy_mgr;
    struct zwlr_virtual_pointer_manager_v1 *vptr_mgr;
    struct zwp_virtual_keyboard_manager_v1 *vkbd_mgr;

    output_entry_t outputs[MAX_OUTPUTS];
    int output_count;

    // Reusable SHM buffer for screencopy
    int shm_fd;
    size_t shm_size;
    void *shm_map;
    struct wl_shm_pool *shm_pool;

    // Reusable virtual pointer / keyboard
    struct zwlr_virtual_pointer_v1 *vptr;
    char vptr_output_name[64];
    struct zwp_virtual_keyboard_v1 *vkbd;
    bool vkbd_keymap_sent;
} wl_ctx_t;

static wl_ctx_t g_ctx = { .lock = PTHREAD_MUTEX_INITIALIZER, .shm_fd = -1 };

static uint32_t now_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint32_t)(ts.tv_sec * 1000u + ts.tv_nsec / 1000000u);
}

static void output_handle_geometry(void *data, struct wl_output *o,
    int32_t x, int32_t y, int32_t pw, int32_t ph, int32_t sub,
    const char *make, const char *model, int32_t transform) {
    (void)data; (void)o; (void)x; (void)y; (void)pw; (void)ph;
    (void)sub; (void)make; (void)model; (void)transform;
}

static void output_handle_mode(void *data, struct wl_output *o,
    uint32_t flags, int32_t w, int32_t h, int32_t refresh) {
    (void)o; (void)refresh;
    output_entry_t *e = (output_entry_t *)data;
    if (flags & WL_OUTPUT_MODE_CURRENT) {
        e->mode_w = w;
        e->mode_h = h;
    }
}

static void output_handle_done(void *data, struct wl_output *o) {
    (void)data; (void)o;
}

static void output_handle_scale(void *data, struct wl_output *o, int32_t factor) {
    (void)o;
    output_entry_t *e = (output_entry_t *)data;
    e->scale = factor > 0 ? factor : 1;
}

static void output_handle_name(void *data, struct wl_output *o, const char *name) {
    (void)o;
    output_entry_t *e = (output_entry_t *)data;
    if (name) {
        snprintf(e->name, sizeof(e->name), "%s", name);
    }
}

static void output_handle_description(void *data, struct wl_output *o, const char *desc) {
    (void)data; (void)o; (void)desc;
}

static const struct wl_output_listener output_listener = {
    .geometry = output_handle_geometry,
    .mode = output_handle_mode,
    .done = output_handle_done,
    .scale = output_handle_scale,
    .name = output_handle_name,
    .description = output_handle_description,
};

static void registry_global(void *data, struct wl_registry *reg,
    uint32_t name, const char *iface, uint32_t version) {
    wl_ctx_t *ctx = (wl_ctx_t *)data;
    if (strcmp(iface, wl_shm_interface.name) == 0) {
        ctx->shm = wl_registry_bind(reg, name, &wl_shm_interface, 1);
    } else if (strcmp(iface, wl_seat_interface.name) == 0 && !ctx->seat) {
        uint32_t v = version < 7 ? version : 7;
        ctx->seat = wl_registry_bind(reg, name, &wl_seat_interface, v);
    } else if (strcmp(iface, wl_output_interface.name) == 0) {
        if (ctx->output_count < MAX_OUTPUTS) {
            output_entry_t *e = &ctx->outputs[ctx->output_count++];
            memset(e, 0, sizeof(*e));
            e->global_name = name;
            e->scale = 1;
            uint32_t v = version < 4 ? version : 4;
            e->output = wl_registry_bind(reg, name, &wl_output_interface, v);
            wl_output_add_listener(e->output, &output_listener, e);
        }
    } else if (strcmp(iface, zwlr_screencopy_manager_v1_interface.name) == 0) {
        uint32_t v = version < 3 ? version : 3;
        ctx->screencopy_mgr = wl_registry_bind(reg, name, &zwlr_screencopy_manager_v1_interface, v);
    } else if (strcmp(iface, zwlr_virtual_pointer_manager_v1_interface.name) == 0) {
        uint32_t v = version < 2 ? version : 2;
        ctx->vptr_mgr = wl_registry_bind(reg, name, &zwlr_virtual_pointer_manager_v1_interface, v);
    } else if (strcmp(iface, zwp_virtual_keyboard_manager_v1_interface.name) == 0) {
        ctx->vkbd_mgr = wl_registry_bind(reg, name, &zwp_virtual_keyboard_manager_v1_interface, 1);
    }
}

static void registry_global_remove(void *data, struct wl_registry *reg, uint32_t name) {
    (void)reg;
    wl_ctx_t *ctx = (wl_ctx_t *)data;
    for (int i = 0; i < ctx->output_count; i++) {
        if (ctx->outputs[i].global_name == name) {
            if (ctx->outputs[i].output) wl_output_destroy(ctx->outputs[i].output);
            for (int j = i + 1; j < ctx->output_count; j++)
                ctx->outputs[j - 1] = ctx->outputs[j];
            ctx->output_count--;
            break;
        }
    }
}

static const struct wl_registry_listener registry_listener = {
    .global = registry_global,
    .global_remove = registry_global_remove,
};

static bool ensure_init_locked(void) {
    if (g_ctx.initialized && g_ctx.display) return true;
    g_ctx.display = wl_display_connect(NULL);
    if (!g_ctx.display) return false;
    g_ctx.registry = wl_display_get_registry(g_ctx.display);
    wl_registry_add_listener(g_ctx.registry, &registry_listener, &g_ctx);
    wl_display_roundtrip(g_ctx.display);
    wl_display_roundtrip(g_ctx.display);
    g_ctx.initialized = true;
    return true;
}

static output_entry_t *find_output_locked(const char *name) {
    if (g_ctx.output_count == 0) return NULL;
    if (!name || !name[0]) return &g_ctx.outputs[0];
    for (int i = 0; i < g_ctx.output_count; i++) {
        if (strcmp(g_ctx.outputs[i].name, name) == 0)
            return &g_ctx.outputs[i];
    }
    return &g_ctx.outputs[0];
}

int sh_wl_init(void) {
    pthread_mutex_lock(&g_ctx.lock);
    bool ok = ensure_init_locked();
    pthread_mutex_unlock(&g_ctx.lock);
    return ok ? 0 : -1;
}

int sh_wl_has_screencopy(void) {
    pthread_mutex_lock(&g_ctx.lock);
    bool ok = ensure_init_locked() && g_ctx.screencopy_mgr && g_ctx.shm && g_ctx.output_count > 0;
    pthread_mutex_unlock(&g_ctx.lock);
    return ok ? 1 : 0;
}

int sh_wl_has_virtual_pointer(void) {
    pthread_mutex_lock(&g_ctx.lock);
    bool ok = ensure_init_locked() && g_ctx.vptr_mgr;
    pthread_mutex_unlock(&g_ctx.lock);
    return ok ? 1 : 0;
}

int sh_wl_has_virtual_keyboard(void) {
    pthread_mutex_lock(&g_ctx.lock);
    bool ok = ensure_init_locked() && g_ctx.vkbd_mgr && g_ctx.seat;
    pthread_mutex_unlock(&g_ctx.lock);
    return ok ? 1 : 0;
}

typedef struct {
    bool has_buffer;
    bool ready;
    bool failed;
    uint32_t format;
    uint32_t width;
    uint32_t height;
    uint32_t stride;
    uint32_t flags;
} frame_state_t;

static void frame_handle_buffer(void *data, struct zwlr_screencopy_frame_v1 *frame,
    uint32_t format, uint32_t width, uint32_t height, uint32_t stride) {
    (void)frame;
    frame_state_t *st = (frame_state_t *)data;
    // Prefer 32-bit RGB/BGR formats
    if (!st->has_buffer || format == WL_SHM_FORMAT_ARGB8888 || format == WL_SHM_FORMAT_XRGB8888
        || format == WL_SHM_FORMAT_ABGR8888 || format == WL_SHM_FORMAT_XBGR8888) {
        st->format = format;
        st->width = width;
        st->height = height;
        st->stride = stride;
        st->has_buffer = true;
    }
}

static void frame_handle_flags(void *data, struct zwlr_screencopy_frame_v1 *frame, uint32_t flags) {
    (void)frame;
    ((frame_state_t *)data)->flags = flags;
}

static void frame_handle_ready(void *data, struct zwlr_screencopy_frame_v1 *frame,
    uint32_t tv_sec_hi, uint32_t tv_sec_lo, uint32_t tv_nsec) {
    (void)frame; (void)tv_sec_hi; (void)tv_sec_lo; (void)tv_nsec;
    ((frame_state_t *)data)->ready = true;
}

static void frame_handle_failed(void *data, struct zwlr_screencopy_frame_v1 *frame) {
    (void)frame;
    ((frame_state_t *)data)->failed = true;
}

static void frame_handle_damage(void *data, struct zwlr_screencopy_frame_v1 *frame,
    uint32_t x, uint32_t y, uint32_t w, uint32_t h) {
    (void)data; (void)frame; (void)x; (void)y; (void)w; (void)h;
}

static void frame_handle_linux_dmabuf(void *data, struct zwlr_screencopy_frame_v1 *frame,
    uint32_t format, uint32_t w, uint32_t h) {
    (void)data; (void)frame; (void)format; (void)w; (void)h;
}

static void frame_handle_buffer_done(void *data, struct zwlr_screencopy_frame_v1 *frame) {
    (void)data; (void)frame;
}

static const struct zwlr_screencopy_frame_v1_listener frame_listener = {
    .buffer = frame_handle_buffer,
    .flags = frame_handle_flags,
    .ready = frame_handle_ready,
    .failed = frame_handle_failed,
    .damage = frame_handle_damage,
    .linux_dmabuf = frame_handle_linux_dmabuf,
    .buffer_done = frame_handle_buffer_done,
};

static bool ensure_shm_locked(size_t needed) {
    if (g_ctx.shm_pool && g_ctx.shm_size >= needed) return true;
    if (g_ctx.shm_pool) { wl_shm_pool_destroy(g_ctx.shm_pool); g_ctx.shm_pool = NULL; }
    if (g_ctx.shm_map && g_ctx.shm_map != MAP_FAILED) { munmap(g_ctx.shm_map, g_ctx.shm_size); g_ctx.shm_map = NULL; }
    if (g_ctx.shm_fd >= 0) { close(g_ctx.shm_fd); g_ctx.shm_fd = -1; }
    g_ctx.shm_size = 0;

    int fd = memfd_create("stronghold-shm", MFD_CLOEXEC);
    if (fd < 0) return false;
    if (ftruncate(fd, (off_t)needed) < 0) { close(fd); return false; }
    void *map = mmap(NULL, needed, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (map == MAP_FAILED) { close(fd); return false; }

    g_ctx.shm_fd = fd;
    g_ctx.shm_size = needed;
    g_ctx.shm_map = map;
    g_ctx.shm_pool = wl_shm_create_pool(g_ctx.shm, fd, (int32_t)needed);
    return g_ctx.shm_pool != NULL;
}

int sh_wl_capture_region(const char *output_name, int32_t x, int32_t y, int32_t w, int32_t h,
    uint8_t *out_bgra, int32_t *out_w, int32_t *out_h, int32_t max_bytes) {
    pthread_mutex_lock(&g_ctx.lock);
    if (!ensure_init_locked() || !g_ctx.screencopy_mgr || !g_ctx.shm) {
        pthread_mutex_unlock(&g_ctx.lock);
        return -1;
    }
    output_entry_t *entry = find_output_locked(output_name);
    if (!entry || !entry->output) {
        pthread_mutex_unlock(&g_ctx.lock);
        return -2;
    }

    struct zwlr_screencopy_frame_v1 *frame;
    if (w > 0 && h > 0) {
        frame = zwlr_screencopy_manager_v1_capture_output_region(
            g_ctx.screencopy_mgr, 0 /* no cursor */, entry->output, x, y, w, h);
    } else {
        frame = zwlr_screencopy_manager_v1_capture_output(
            g_ctx.screencopy_mgr, 0 /* no cursor */, entry->output);
    }
    if (!frame) {
        pthread_mutex_unlock(&g_ctx.lock);
        return -3;
    }

    frame_state_t st;
    memset(&st, 0, sizeof(st));
    zwlr_screencopy_frame_v1_add_listener(frame, &frame_listener, &st);
    wl_display_roundtrip(g_ctx.display);

    if (!st.has_buffer || st.failed || st.width == 0 || st.height == 0) {
        zwlr_screencopy_frame_v1_destroy(frame);
        pthread_mutex_unlock(&g_ctx.lock);
        return -4;
    }

    size_t needed = (size_t)st.stride * (size_t)st.height;
    size_t out_needed = (size_t)st.width * (size_t)st.height * 4;
    if (out_w) *out_w = (int32_t)st.width;
    if (out_h) *out_h = (int32_t)st.height;

    if (!out_bgra || (size_t)max_bytes < out_needed) {
        zwlr_screencopy_frame_v1_destroy(frame);
        pthread_mutex_unlock(&g_ctx.lock);
        return -5; // Buffer too small; out_w/out_h populated
    }

    if (!ensure_shm_locked(needed)) {
        zwlr_screencopy_frame_v1_destroy(frame);
        pthread_mutex_unlock(&g_ctx.lock);
        return -6;
    }

    struct wl_buffer *buffer = wl_shm_pool_create_buffer(
        g_ctx.shm_pool, 0, (int32_t)st.width, (int32_t)st.height, (int32_t)st.stride, st.format);
    if (!buffer) {
        zwlr_screencopy_frame_v1_destroy(frame);
        pthread_mutex_unlock(&g_ctx.lock);
        return -7;
    }

    zwlr_screencopy_frame_v1_copy(frame, buffer);
    while (!st.ready && !st.failed) {
        if (wl_display_dispatch(g_ctx.display) < 0) {
            st.failed = true;
            break;
        }
    }

    int rc = 0;
    if (!st.ready || st.failed) {
        rc = -8;
    } else {
        bool invert_y = (st.flags & ZWLR_SCREENCOPY_FRAME_V1_FLAGS_Y_INVERT) != 0;
        bool swap_rb = (st.format == WL_SHM_FORMAT_ABGR8888 || st.format == WL_SHM_FORMAT_XBGR8888);
        const uint8_t *src_base = (const uint8_t *)g_ctx.shm_map;
        for (uint32_t row = 0; row < st.height; row++) {
            uint32_t src_row = invert_y ? (st.height - 1 - row) : row;
            const uint8_t *s = src_base + (size_t)src_row * st.stride;
            uint8_t *d = out_bgra + (size_t)row * st.width * 4;
            if (!swap_rb) {
                // ARGB8888 / XRGB8888 on little-endian is already BGRA in memory!
                memcpy(d, s, (size_t)st.width * 4);
                for (uint32_t col = 0; col < st.width; col++) d[col * 4 + 3] = 255;
            } else {
                // ABGR8888 / XBGR8888 on little-endian is RGBA -> convert to BGRA
                for (uint32_t col = 0; col < st.width; col++) {
                    d[col * 4 + 0] = s[col * 4 + 2];
                    d[col * 4 + 1] = s[col * 4 + 1];
                    d[col * 4 + 2] = s[col * 4 + 0];
                    d[col * 4 + 3] = 255;
                }
            }
        }
    }

    wl_buffer_destroy(buffer);
    zwlr_screencopy_frame_v1_destroy(frame);
    pthread_mutex_unlock(&g_ctx.lock);
    return rc;
}

static bool ensure_vptr_locked(const char *output_name) {
    if (!ensure_init_locked() || !g_ctx.vptr_mgr) return false;
    const char *want = output_name ? output_name : "";
    if (g_ctx.vptr && strcmp(g_ctx.vptr_output_name, want) == 0) return true;
    if (g_ctx.vptr) {
        zwlr_virtual_pointer_v1_destroy(g_ctx.vptr);
        g_ctx.vptr = NULL;
        g_ctx.vptr_output_name[0] = '\0';
    }
    output_entry_t *e = (want[0] != '\0') ? find_output_locked(want) : NULL;
    if (e && e->output) {
        g_ctx.vptr = zwlr_virtual_pointer_manager_v1_create_virtual_pointer_with_output(
            g_ctx.vptr_mgr, g_ctx.seat, e->output);
        snprintf(g_ctx.vptr_output_name, sizeof(g_ctx.vptr_output_name), "%s", want);
    } else {
        g_ctx.vptr = zwlr_virtual_pointer_manager_v1_create_virtual_pointer(
            g_ctx.vptr_mgr, g_ctx.seat);
        g_ctx.vptr_output_name[0] = '\0';
    }
    wl_display_roundtrip(g_ctx.display);
    return g_ctx.vptr != NULL;
}

int sh_wl_pointer_move_abs(const char *output_name, uint32_t x, uint32_t y, uint32_t x_extent, uint32_t y_extent) {
    pthread_mutex_lock(&g_ctx.lock);
    if (!ensure_vptr_locked(output_name)) { pthread_mutex_unlock(&g_ctx.lock); return -1; }
    zwlr_virtual_pointer_v1_motion_absolute(g_ctx.vptr, now_ms(), x, y, x_extent, y_extent);
    zwlr_virtual_pointer_v1_frame(g_ctx.vptr);
    wl_display_flush(g_ctx.display);
    pthread_mutex_unlock(&g_ctx.lock);
    return 0;
}

int sh_wl_pointer_move_rel(double dx, double dy) {
    pthread_mutex_lock(&g_ctx.lock);
    if (!ensure_vptr_locked(g_ctx.vptr_output_name)) { pthread_mutex_unlock(&g_ctx.lock); return -1; }
    zwlr_virtual_pointer_v1_motion(g_ctx.vptr, now_ms(), wl_fixed_from_double(dx), wl_fixed_from_double(dy));
    zwlr_virtual_pointer_v1_frame(g_ctx.vptr);
    wl_display_flush(g_ctx.display);
    pthread_mutex_unlock(&g_ctx.lock);
    return 0;
}

int sh_wl_pointer_button(uint32_t linux_btn, int pressed) {
    pthread_mutex_lock(&g_ctx.lock);
    if (!ensure_vptr_locked(g_ctx.vptr_output_name)) { pthread_mutex_unlock(&g_ctx.lock); return -1; }
    zwlr_virtual_pointer_v1_button(g_ctx.vptr, now_ms(), linux_btn,
        pressed ? WL_POINTER_BUTTON_STATE_PRESSED : WL_POINTER_BUTTON_STATE_RELEASED);
    zwlr_virtual_pointer_v1_frame(g_ctx.vptr);
    wl_display_flush(g_ctx.display);
    pthread_mutex_unlock(&g_ctx.lock);
    return 0;
}

int sh_wl_pointer_scroll(double value_y) {
    pthread_mutex_lock(&g_ctx.lock);
    if (!ensure_vptr_locked(g_ctx.vptr_output_name)) { pthread_mutex_unlock(&g_ctx.lock); return -1; }
    zwlr_virtual_pointer_v1_axis_source(g_ctx.vptr, WL_POINTER_AXIS_SOURCE_WHEEL);
    zwlr_virtual_pointer_v1_axis(g_ctx.vptr, now_ms(), WL_POINTER_AXIS_VERTICAL_SCROLL, wl_fixed_from_double(value_y));
    zwlr_virtual_pointer_v1_frame(g_ctx.vptr);
    wl_display_flush(g_ctx.display);
    pthread_mutex_unlock(&g_ctx.lock);
    return 0;
}

// Minimal XKB keymap covering evdev keys for digits, A-Z, Escape, Space, Return, Tab
static const char MINIMAL_XKB_KEYMAP[] =
    "xkb_keymap {\n"
    "xkb_keycodes \"evdev\" {\n"
    "  minimum = 8; maximum = 255;\n"
    "  <ESC> = 9;\n"
    "  <AE01> = 10; <AE02> = 11; <AE03> = 12; <AE04> = 13; <AE05> = 14;\n"
    "  <AE06> = 15; <AE07> = 16; <AE08> = 17; <AE09> = 18; <AE10> = 19;\n"
    "  <TAB> = 23;\n"
    "  <AD01> = 24; <AD02> = 25; <AD03> = 26; <AD04> = 27; <AD05> = 28;\n"
    "  <AD06> = 29; <AD07> = 30; <AD08> = 31; <AD09> = 32; <AD10> = 33;\n"
    "  <RTRN> = 36;\n"
    "  <AC01> = 38; <AC02> = 39; <AC03> = 40; <AC04> = 41; <AC05> = 42;\n"
    "  <AC06> = 43; <AC07> = 44; <AC08> = 45; <AC09> = 46;\n"
    "  <AB01> = 52; <AB02> = 53; <AB03> = 54; <AB04> = 55; <AB05> = 56;\n"
    "  <AB06> = 57; <AB07> = 58;\n"
    "  <SPCE> = 65;\n"
    "};\n"
    "xkb_types \"basic\" {\n"
    "  virtual_modifiers NumLock;\n"
    "  type \"ONE_LEVEL\" { modifiers = none; level_name[Level1] = \"Any\"; };\n"
    "};\n"
    "xkb_compatibility \"basic\" { interpret.repeat = False; };\n"
    "xkb_symbols \"us\" {\n"
    "  key <ESC>  { [ Escape ] };\n"
    "  key <AE01> { [ 1 ] }; key <AE02> { [ 2 ] }; key <AE03> { [ 3 ] }; key <AE04> { [ 4 ] }; key <AE05> { [ 5 ] };\n"
    "  key <AE06> { [ 6 ] }; key <AE07> { [ 7 ] }; key <AE08> { [ 8 ] }; key <AE09> { [ 9 ] }; key <AE10> { [ 0 ] };\n"
    "  key <TAB>  { [ Tab ] }; key <RTRN> { [ Return ] }; key <SPCE> { [ space ] };\n"
    "  key <AD01> { [ q ] }; key <AD02> { [ w ] }; key <AD03> { [ e ] }; key <AD04> { [ r ] }; key <AD05> { [ t ] };\n"
    "  key <AD06> { [ y ] }; key <AD07> { [ u ] }; key <AD08> { [ i ] }; key <AD09> { [ o ] }; key <AD10> { [ p ] };\n"
    "  key <AC01> { [ a ] }; key <AC02> { [ s ] }; key <AC03> { [ d ] }; key <AC04> { [ f ] }; key <AC05> { [ g ] };\n"
    "  key <AC06> { [ h ] }; key <AC07> { [ j ] }; key <AC08> { [ k ] }; key <AC09> { [ l ] };\n"
    "  key <AB01> { [ z ] }; key <AB02> { [ x ] }; key <AB03> { [ c ] }; key <AB04> { [ v ] }; key <AB05> { [ b ] };\n"
    "  key <AB06> { [ n ] }; key <AB07> { [ m ] };\n"
    "};\n"
    "};\n";

int sh_wl_keyboard_key(uint32_t evdev_keycode, int pressed) {
    pthread_mutex_lock(&g_ctx.lock);
    if (!ensure_init_locked() || !g_ctx.vkbd_mgr || !g_ctx.seat) {
        pthread_mutex_unlock(&g_ctx.lock);
        return -1;
    }
    if (!g_ctx.vkbd) {
        g_ctx.vkbd = zwp_virtual_keyboard_manager_v1_create_virtual_keyboard(g_ctx.vkbd_mgr, g_ctx.seat);
        if (!g_ctx.vkbd) { pthread_mutex_unlock(&g_ctx.lock); return -2; }
    }
    if (!g_ctx.vkbd_keymap_sent) {
        int fd = memfd_create("stronghold-keymap", MFD_CLOEXEC);
        if (fd < 0) { pthread_mutex_unlock(&g_ctx.lock); return -3; }
        size_t len = sizeof(MINIMAL_XKB_KEYMAP);
        if (write(fd, MINIMAL_XKB_KEYMAP, len) != (ssize_t)len) {
            close(fd);
            pthread_mutex_unlock(&g_ctx.lock);
            return -4;
        }
        lseek(fd, 0, SEEK_SET);
        zwp_virtual_keyboard_v1_keymap(g_ctx.vkbd, WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1, fd, (uint32_t)len);
        close(fd);
        g_ctx.vkbd_keymap_sent = true;
        wl_display_roundtrip(g_ctx.display);
    }
    zwp_virtual_keyboard_v1_key(g_ctx.vkbd, now_ms(), evdev_keycode,
        pressed ? WL_KEYBOARD_KEY_STATE_PRESSED : WL_KEYBOARD_KEY_STATE_RELEASED);
    wl_display_flush(g_ctx.display);
    pthread_mutex_unlock(&g_ctx.lock);
    return 0;
}

void sh_wl_shutdown(void) {
    pthread_mutex_lock(&g_ctx.lock);
    if (g_ctx.vptr) { zwlr_virtual_pointer_v1_destroy(g_ctx.vptr); g_ctx.vptr = NULL; }
    if (g_ctx.vkbd) { zwp_virtual_keyboard_v1_destroy(g_ctx.vkbd); g_ctx.vkbd = NULL; g_ctx.vkbd_keymap_sent = false; }
    if (g_ctx.shm_pool) { wl_shm_pool_destroy(g_ctx.shm_pool); g_ctx.shm_pool = NULL; }
    if (g_ctx.shm_map && g_ctx.shm_map != MAP_FAILED) { munmap(g_ctx.shm_map, g_ctx.shm_size); g_ctx.shm_map = NULL; }
    if (g_ctx.shm_fd >= 0) { close(g_ctx.shm_fd); g_ctx.shm_fd = -1; }
    for (int i = 0; i < g_ctx.output_count; i++) {
        if (g_ctx.outputs[i].output) wl_output_destroy(g_ctx.outputs[i].output);
    }
    g_ctx.output_count = 0;
    if (g_ctx.screencopy_mgr) { zwlr_screencopy_manager_v1_destroy(g_ctx.screencopy_mgr); g_ctx.screencopy_mgr = NULL; }
    if (g_ctx.vptr_mgr) { zwlr_virtual_pointer_manager_v1_destroy(g_ctx.vptr_mgr); g_ctx.vptr_mgr = NULL; }
    if (g_ctx.vkbd_mgr) { zwp_virtual_keyboard_manager_v1_destroy(g_ctx.vkbd_mgr); g_ctx.vkbd_mgr = NULL; }
    if (g_ctx.seat) { wl_seat_destroy(g_ctx.seat); g_ctx.seat = NULL; }
    if (g_ctx.shm) { wl_shm_destroy(g_ctx.shm); g_ctx.shm = NULL; }
    if (g_ctx.registry) { wl_registry_destroy(g_ctx.registry); g_ctx.registry = NULL; }
    if (g_ctx.display) { wl_display_disconnect(g_ctx.display); g_ctx.display = NULL; }
    g_ctx.initialized = false;
    pthread_mutex_unlock(&g_ctx.lock);
}
