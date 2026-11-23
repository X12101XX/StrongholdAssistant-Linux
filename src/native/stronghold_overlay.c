#define _GNU_SOURCE
#include <cairo.h>
#include <gtk/gtk.h>
#include <gtk4-layer-shell.h>
#include <math.h>
#include <pango/pangocairo.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MAX_OPS 2048
#define MAX_POLY_PTS 64

typedef enum {
    OP_RECT,
    OP_POLY,
    OP_CIRCLE,
    OP_TEXT
} op_kind_t;

typedef struct {
    op_kind_t kind;
    double x, y, w, h;
    double stroke_r, stroke_g, stroke_b, stroke_a, stroke_w;
    double fill_r, fill_g, fill_b, fill_a;
    int dashed;
    int closed;
    int bold;
    int align_center;
    double font_px;
    double pad_x, pad_y;
    int npts;
    double pts[MAX_POLY_PTS * 2];
    char text[256];
} draw_op_t;

typedef struct {
    GtkWindow *win;
    GtkWidget *area;
    char current_output[64];
    int vp_x, vp_y, vp_w, vp_h;
    bool visible;

    draw_op_t pending[MAX_OPS];
    int pending_count;

    draw_op_t active[MAX_OPS];
    int active_count;

    char line_buf[8192];
    size_t line_len;
} overlay_state_t;

static overlay_state_t g_ov;

static void apply_click_through(GtkWindow *win) {
    GtkNative *native = GTK_NATIVE(win);
    if (!native) return;
    GdkSurface *surf = gtk_native_get_surface(native);
    if (!surf) return;
    cairo_region_t *empty = cairo_region_create();
    gdk_surface_set_input_region(surf, empty);
    cairo_region_destroy(empty);
}

static GdkMonitor *find_monitor_by_connector(const char *name) {
    GdkDisplay *display = gdk_display_get_default();
    if (!display) return NULL;
    GListModel *monitors = gdk_display_get_monitors(display);
    guint n = g_list_model_get_n_items(monitors);
    GdkMonitor *first = NULL;
    for (guint i = 0; i < n; i++) {
        GdkMonitor *m = GDK_MONITOR(g_list_model_get_item(monitors, i));
        if (!first) first = m;
        const char *conn = gdk_monitor_get_connector(m);
        if (name && name[0] && conn && strcmp(conn, name) == 0) {
            if (first != m) g_object_unref(first);
            return m;
        }
        if (m != first) g_object_unref(m);
    }
    return first;
}

static void on_draw(GtkDrawingArea *area, cairo_t *cr, int width, int height, gpointer user_data) {
    (void)area; (void)width; (void)height; (void)user_data;

    cairo_save(cr);
    cairo_set_operator(cr, CAIRO_OPERATOR_CLEAR);
    cairo_paint(cr);
    cairo_restore(cr);

    if (!g_ov.visible || g_ov.active_count == 0) return;

    cairo_save(cr);
    cairo_translate(cr, g_ov.vp_x, g_ov.vp_y);

    for (int i = 0; i < g_ov.active_count; i++) {
        const draw_op_t *op = &g_ov.active[i];
        cairo_save(cr);
        if (op->kind == OP_RECT) {
            if (op->fill_a > 0.001) {
                cairo_set_source_rgba(cr, op->fill_r, op->fill_g, op->fill_b, op->fill_a);
                cairo_rectangle(cr, op->x, op->y, op->w, op->h);
                cairo_fill(cr);
            }
            if (op->stroke_a > 0.001 && op->stroke_w > 0.0) {
                cairo_set_source_rgba(cr, op->stroke_r, op->stroke_g, op->stroke_b, op->stroke_a);
                cairo_set_line_width(cr, op->stroke_w);
                if (op->dashed) {
                    double dashes[2] = { op->stroke_w * 3.5, op->stroke_w * 2.5 };
                    cairo_set_dash(cr, dashes, 2, 0.0);
                }
                cairo_rectangle(cr, op->x, op->y, op->w, op->h);
                cairo_stroke(cr);
            }
        } else if (op->kind == OP_POLY && op->npts >= 2) {
            cairo_new_path(cr);
            cairo_move_to(cr, op->pts[0], op->pts[1]);
            for (int p = 1; p < op->npts; p++) {
                cairo_line_to(cr, op->pts[p * 2], op->pts[p * 2 + 1]);
            }
            if (op->closed) cairo_close_path(cr);

            if (op->closed && op->fill_a > 0.001) {
                cairo_set_source_rgba(cr, op->fill_r, op->fill_g, op->fill_b, op->fill_a);
                cairo_fill_preserve(cr);
            }
            if (op->stroke_a > 0.001 && op->stroke_w > 0.0) {
                cairo_set_source_rgba(cr, op->stroke_r, op->stroke_g, op->stroke_b, op->stroke_a);
                cairo_set_line_width(cr, op->stroke_w);
                cairo_set_line_cap(cr, CAIRO_LINE_CAP_ROUND);
                cairo_set_line_join(cr, CAIRO_LINE_JOIN_ROUND);
                if (op->dashed) {
                    double dashes[2] = { op->stroke_w * 3.0, op->stroke_w * 2.2 };
                    cairo_set_dash(cr, dashes, 2, 0.0);
                }
                cairo_stroke(cr);
            } else {
                cairo_new_path(cr);
            }
        } else if (op->kind == OP_CIRCLE) {
            cairo_arc(cr, op->x, op->y, op->w, 0, 2.0 * M_PI);
            if (op->fill_a > 0.001) {
                cairo_set_source_rgba(cr, op->fill_r, op->fill_g, op->fill_b, op->fill_a);
                cairo_fill_preserve(cr);
            }
            if (op->stroke_a > 0.001 && op->stroke_w > 0.0) {
                cairo_set_source_rgba(cr, op->stroke_r, op->stroke_g, op->stroke_b, op->stroke_a);
                cairo_set_line_width(cr, op->stroke_w);
                cairo_stroke(cr);
            } else {
                cairo_new_path(cr);
            }
        } else if (op->kind == OP_TEXT && op->text[0]) {
            PangoLayout *layout = pango_cairo_create_layout(cr);
            PangoFontDescription *desc = pango_font_description_new();
            pango_font_description_set_family(desc, "Noto Sans CJK SC, Sans");
            pango_font_description_set_absolute_size(desc, op->font_px * PANGO_SCALE);
            pango_font_description_set_weight(desc, op->bold ? PANGO_WEIGHT_BOLD : PANGO_WEIGHT_NORMAL);
            pango_layout_set_font_description(layout, desc);
            pango_layout_set_text(layout, op->text, -1);

            int tw = 0, th = 0;
            pango_layout_get_pixel_size(layout, &tw, &th);
            double bx = op->align_center ? (op->x - (tw + op->pad_x * 2.0) * 0.5) : op->x;
            double by = op->align_center ? (op->y - (th + op->pad_y * 2.0) * 0.5) : op->y;

            if (op->fill_a > 0.001) {
                double bw = tw + op->pad_x * 2.0;
                double bh = th + op->pad_y * 2.0;
                double r = 4.0;
                cairo_new_sub_path(cr);
                cairo_arc(cr, bx + bw - r, by + r, r, -M_PI / 2, 0);
                cairo_arc(cr, bx + bw - r, by + bh - r, r, 0, M_PI / 2);
                cairo_arc(cr, bx + r, by + bh - r, r, M_PI / 2, M_PI);
                cairo_arc(cr, bx + r, by + r, r, M_PI, 3 * M_PI / 2);
                cairo_close_path(cr);
                cairo_set_source_rgba(cr, op->fill_r, op->fill_g, op->fill_b, op->fill_a);
                cairo_fill(cr);
            }

            cairo_set_source_rgba(cr, op->stroke_r, op->stroke_g, op->stroke_b, op->stroke_a);
            cairo_move_to(cr, bx + op->pad_x, by + op->pad_y);
            pango_cairo_show_layout(cr, layout);

            pango_font_description_free(desc);
            g_object_unref(layout);
        }
        cairo_restore(cr);
    }

    cairo_restore(cr);
}

static void handle_command(const char *line) {
    if (strncmp(line, "QUIT", 4) == 0) {
        g_application_quit(G_APPLICATION(gtk_window_get_application(g_ov.win)));
        return;
    }
    if (strncmp(line, "HIDE", 4) == 0) {
        g_ov.visible = false;
        g_ov.active_count = 0;
        gtk_widget_set_visible(GTK_WIDGET(g_ov.win), FALSE);
        return;
    }
    if (strncmp(line, "CLEAR", 5) == 0) {
        g_ov.pending_count = 0;
        return;
    }
    if (strncmp(line, "SHOW ", 5) == 0) {
        char out_name[64] = {0};
        int x = 0, y = 0, w = 1920, h = 1080;
        if (sscanf(line + 5, "%63s %d %d %d %d", out_name, &x, &y, &w, &h) == 5) {
            bool output_changed = strcmp(g_ov.current_output, out_name) != 0;
            g_ov.vp_x = x;
            g_ov.vp_y = y;
            g_ov.vp_w = w;
            g_ov.vp_h = h;
            if (output_changed) {
                snprintf(g_ov.current_output, sizeof(g_ov.current_output), "%s", out_name);
                GdkMonitor *mon = find_monitor_by_connector(out_name);
                if (mon) {
                    gtk_layer_set_monitor(g_ov.win, mon);
                    g_object_unref(mon);
                }
            }
            g_ov.visible = true;
            if (!gtk_widget_get_visible(GTK_WIDGET(g_ov.win))) {
                gtk_widget_set_visible(GTK_WIDGET(g_ov.win), TRUE);
                apply_click_through(g_ov.win);
            }
            gtk_widget_queue_draw(g_ov.area);
        }
        return;
    }
    if (strncmp(line, "COMMIT", 6) == 0) {
        memcpy(g_ov.active, g_ov.pending, (size_t)g_ov.pending_count * sizeof(draw_op_t));
        g_ov.active_count = g_ov.pending_count;
        g_ov.pending_count = 0;
        if (g_ov.active_count == 0) {
            g_ov.visible = false;
            gtk_widget_set_visible(GTK_WIDGET(g_ov.win), FALSE);
        } else if (g_ov.visible) {
            if (!gtk_widget_get_visible(GTK_WIDGET(g_ov.win))) {
                gtk_widget_set_visible(GTK_WIDGET(g_ov.win), TRUE);
                apply_click_through(g_ov.win);
            }
            gtk_widget_queue_draw(g_ov.area);
        }
        return;
    }
    if (g_ov.pending_count >= MAX_OPS) return;
    draw_op_t *op = &g_ov.pending[g_ov.pending_count];
    memset(op, 0, sizeof(*op));

    if (strncmp(line, "RECT ", 5) == 0) {
        op->kind = OP_RECT;
        if (sscanf(line + 5, "%lf %lf %lf %lf %lf %lf %lf %lf %lf %d %lf %lf %lf %lf",
            &op->x, &op->y, &op->w, &op->h,
            &op->stroke_r, &op->stroke_g, &op->stroke_b, &op->stroke_a, &op->stroke_w, &op->dashed,
            &op->fill_r, &op->fill_g, &op->fill_b, &op->fill_a) == 14) {
            g_ov.pending_count++;
        }
    } else if (strncmp(line, "CIRCLE ", 7) == 0) {
        op->kind = OP_CIRCLE;
        if (sscanf(line + 7, "%lf %lf %lf %lf %lf %lf %lf %lf %lf %lf %lf %lf",
            &op->x, &op->y, &op->w,
            &op->stroke_r, &op->stroke_g, &op->stroke_b, &op->stroke_a, &op->stroke_w,
            &op->fill_r, &op->fill_g, &op->fill_b, &op->fill_a) == 12) {
            g_ov.pending_count++;
        }
    } else if (strncmp(line, "POLY ", 5) == 0) {
        op->kind = OP_POLY;
        const char *p = line + 5;
        char *end = NULL;
        int n = (int)strtol(p, &end, 10);
        if (n >= 2 && n <= MAX_POLY_PTS && end != p) {
            op->npts = n;
            p = end;
            bool ok = true;
            for (int i = 0; i < n * 2; i++) {
                op->pts[i] = strtod(p, &end);
                if (end == p) { ok = false; break; }
                p = end;
            }
            if (ok && sscanf(p, "%lf %lf %lf %lf %lf %d %lf %lf %lf %lf %d",
                &op->stroke_r, &op->stroke_g, &op->stroke_b, &op->stroke_a, &op->stroke_w, &op->dashed,
                &op->fill_r, &op->fill_g, &op->fill_b, &op->fill_a, &op->closed) == 11) {
                g_ov.pending_count++;
            }
        }
    } else if (strncmp(line, "TEXT ", 5) == 0) {
        op->kind = OP_TEXT;
        int consumed = 0;
        if (sscanf(line + 5, "%lf %lf %lf %d %lf %lf %lf %lf %lf %lf %lf %lf %lf %lf %d %n",
            &op->x, &op->y, &op->font_px, &op->bold,
            &op->stroke_r, &op->stroke_g, &op->stroke_b, &op->stroke_a,
            &op->fill_r, &op->fill_g, &op->fill_b, &op->fill_a,
            &op->pad_x, &op->pad_y, &op->align_center, &consumed) >= 15 && consumed > 0) {
            snprintf(op->text, sizeof(op->text), "%s", line + 5 + consumed);
            g_ov.pending_count++;
        }
    }
}

static gboolean on_stdin_ready(GIOChannel *source, GIOCondition cond, gpointer data) {
    (void)data;
    if (cond & (G_IO_HUP | G_IO_ERR)) {
        g_application_quit(G_APPLICATION(gtk_window_get_application(g_ov.win)));
        return FALSE;
    }
    char buf[4096];
    gsize bytes_read = 0;
    GError *err = NULL;
    GIOStatus status = g_io_channel_read_chars(source, buf, sizeof(buf), &bytes_read, &err);
    if (err) { g_error_free(err); return TRUE; }
    if (status == G_IO_STATUS_EOF || bytes_read == 0) {
        g_application_quit(G_APPLICATION(gtk_window_get_application(g_ov.win)));
        return FALSE;
    }
    for (gsize i = 0; i < bytes_read; i++) {
        char c = buf[i];
        if (c == '\n') {
            g_ov.line_buf[g_ov.line_len] = '\0';
            if (g_ov.line_len > 0) handle_command(g_ov.line_buf);
            g_ov.line_len = 0;
        } else if (c != '\r' && g_ov.line_len + 1 < sizeof(g_ov.line_buf)) {
            g_ov.line_buf[g_ov.line_len++] = c;
        }
    }
    return TRUE;
}

static void on_map(GtkWidget *w, gpointer data) {
    (void)data;
    apply_click_through(GTK_WINDOW(w));
}

static void activate(GtkApplication *app, gpointer user_data) {
    (void)user_data;
    GtkWidget *win = gtk_application_window_new(app);
    g_ov.win = GTK_WINDOW(win);

    gtk_layer_init_for_window(g_ov.win);
    gtk_layer_set_namespace(g_ov.win, "stronghold-overlay");
    gtk_layer_set_layer(g_ov.win, GTK_LAYER_SHELL_LAYER_OVERLAY);
    gtk_layer_set_keyboard_mode(g_ov.win, GTK_LAYER_SHELL_KEYBOARD_MODE_NONE);
    gtk_layer_set_exclusive_zone(g_ov.win, -1);
    gtk_layer_set_anchor(g_ov.win, GTK_LAYER_SHELL_EDGE_TOP, TRUE);
    gtk_layer_set_anchor(g_ov.win, GTK_LAYER_SHELL_EDGE_BOTTOM, TRUE);
    gtk_layer_set_anchor(g_ov.win, GTK_LAYER_SHELL_EDGE_LEFT, TRUE);
    gtk_layer_set_anchor(g_ov.win, GTK_LAYER_SHELL_EDGE_RIGHT, TRUE);

    GtkCssProvider *css = gtk_css_provider_new();
    gtk_css_provider_load_from_string(css, "window { background: transparent; }");
    gtk_style_context_add_provider_for_display(gdk_display_get_default(),
        GTK_STYLE_PROVIDER(css), GTK_STYLE_PROVIDER_PRIORITY_APPLICATION);
    g_object_unref(css);

    GtkWidget *area = gtk_drawing_area_new();
    g_ov.area = area;
    gtk_drawing_area_set_draw_func(GTK_DRAWING_AREA(area), on_draw, NULL, NULL);
    gtk_window_set_child(g_ov.win, area);

    g_signal_connect(win, "map", G_CALLBACK(on_map), NULL);
    gtk_widget_set_visible(win, FALSE);

    GIOChannel *ch = g_io_channel_unix_new(fileno(stdin));
    g_io_channel_set_encoding(ch, NULL, NULL);
    g_io_channel_set_flags(ch, G_IO_FLAG_NONBLOCK, NULL);
    g_io_add_watch(ch, G_IO_IN | G_IO_HUP | G_IO_ERR, on_stdin_ready, NULL);
    g_io_channel_unref(ch);
}

int main(int argc, char **argv) {
    GtkApplication *app = gtk_application_new("io.github.stronghold.overlay", G_APPLICATION_NON_UNIQUE);
    g_signal_connect(app, "activate", G_CALLBACK(activate), NULL);
    int status = g_application_run(G_APPLICATION(app), argc, argv);
    g_object_unref(app);
    return status;
}
