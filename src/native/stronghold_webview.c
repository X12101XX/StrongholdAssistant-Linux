#define _GNU_SOURCE
#include <gtk/gtk.h>
#include <webkit2/webkit2.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
    GtkWindow *win;
    WebKitWebView *web;
    char url[1024];
    char title[256];
    char app_id[128];
    char data_dir[1024];
    int width;
    int height;
    bool decorated;
    bool dev_tools;
    char line_buf[2048];
    size_t line_len;
} app_ctx_t;

static app_ctx_t g_app = {
    .width = 400,
    .height = 900,
    .decorated = false,
    .dev_tools = false,
};

static void on_script_message(WebKitUserContentManager *mgr, WebKitJavascriptResult *res, gpointer data) {
    (void)mgr; (void)data;
    JSCValue *val = webkit_javascript_result_get_js_value(res);
    if (!val || !jsc_value_is_string(val)) return;
    char *cmd = jsc_value_to_string(val);
    if (!cmd) return;
    if (strcmp(cmd, "minimize") == 0) {
        gtk_window_iconify(g_app.win);
    } else if (strcmp(cmd, "maximize") == 0) {
        gtk_window_maximize(g_app.win);
    } else if (strcmp(cmd, "restore") == 0) {
        gtk_window_unmaximize(g_app.win);
    } else if (strcmp(cmd, "close") == 0) {
        gtk_window_close(g_app.win);
    } else if (strncmp(cmd, "title:", 6) == 0) {
        gtk_window_set_title(g_app.win, cmd + 6);
    } else if (strncmp(cmd, "resize:", 7) == 0) {
        int w = 0, h = 0;
        if (sscanf(cmd + 7, "%d,%d", &w, &h) == 2 && w > 100 && h > 100) {
            gtk_window_resize(g_app.win, w, h);
        }
    }
    g_free(cmd);
}

static void handle_stdin_cmd(const char *line) {
    if (strncmp(line, "QUIT", 4) == 0 || strncmp(line, "CLOSE", 5) == 0) {
        gtk_main_quit();
    } else if (strncmp(line, "MINIMIZE", 8) == 0) {
        gtk_window_iconify(g_app.win);
    } else if (strncmp(line, "MAXIMIZE", 8) == 0) {
        gtk_window_maximize(g_app.win);
    } else if (strncmp(line, "RESTORE", 7) == 0) {
        gtk_window_unmaximize(g_app.win);
    } else if (strncmp(line, "SHOW", 4) == 0) {
        gtk_widget_show(GTK_WIDGET(g_app.win));
        gtk_window_present(g_app.win);
    } else if (strncmp(line, "HIDE", 4) == 0) {
        gtk_widget_hide(GTK_WIDGET(g_app.win));
    } else if (strncmp(line, "RESIZE ", 7) == 0) {
        int w = 0, h = 0;
        if (sscanf(line + 7, "%d %d", &w, &h) == 2 && w >= 60 && h >= 60) {
            gtk_window_resize(g_app.win, w, h);
        }
    } else if (strncmp(line, "NAV ", 4) == 0) {
        webkit_web_view_load_uri(g_app.web, line + 4);
    }
}

static gboolean on_stdin_ready(GIOChannel *source, GIOCondition cond, gpointer data) {
    (void)data;
    if (cond & (G_IO_HUP | G_IO_ERR)) {
        gtk_main_quit();
        return FALSE;
    }
    char buf[1024];
    gsize n = 0;
    GError *err = NULL;
    GIOStatus st = g_io_channel_read_chars(source, buf, sizeof(buf), &n, &err);
    if (err) { g_error_free(err); return TRUE; }
    if (st == G_IO_STATUS_EOF || n == 0) {
        gtk_main_quit();
        return FALSE;
    }
    for (gsize i = 0; i < n; i++) {
        char c = buf[i];
        if (c == '\n') {
            g_app.line_buf[g_app.line_len] = '\0';
            if (g_app.line_len > 0) handle_stdin_cmd(g_app.line_buf);
            g_app.line_len = 0;
        } else if (c != '\r' && g_app.line_len + 1 < sizeof(g_app.line_buf)) {
            g_app.line_buf[g_app.line_len++] = c;
        }
    }
    return TRUE;
}

static gboolean on_decide_policy(WebKitWebView *web, WebKitPolicyDecision *decision,
    WebKitPolicyDecisionType type, gpointer data) {
    (void)web; (void)data;
    if (type == WEBKIT_POLICY_DECISION_TYPE_NEW_WINDOW_ACTION) {
        webkit_policy_decision_ignore(decision);
        return TRUE;
    }
    return FALSE;
}

static gboolean on_context_menu(WebKitWebView *web, WebKitContextMenu *menu,
    GdkEvent *event, WebKitHitTestResult *hit, gpointer data) {
    (void)web; (void)menu; (void)event; (void)hit; (void)data;
    return !g_app.dev_tools;
}

int main(int argc, char **argv) {
    snprintf(g_app.title, sizeof(g_app.title), "高效雅雅·卫戍协议助手");
    snprintf(g_app.app_id, sizeof(g_app.app_id), "stronghold-assistant");
    snprintf(g_app.url, sizeof(g_app.url), "about:blank");

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--url") == 0 && i + 1 < argc) {
            snprintf(g_app.url, sizeof(g_app.url), "%s", argv[++i]);
        } else if (strcmp(argv[i], "--title") == 0 && i + 1 < argc) {
            snprintf(g_app.title, sizeof(g_app.title), "%s", argv[++i]);
        } else if (strcmp(argv[i], "--app-id") == 0 && i + 1 < argc) {
            snprintf(g_app.app_id, sizeof(g_app.app_id), "%s", argv[++i]);
        } else if (strcmp(argv[i], "--data-dir") == 0 && i + 1 < argc) {
            snprintf(g_app.data_dir, sizeof(g_app.data_dir), "%s", argv[++i]);
        } else if (strcmp(argv[i], "--width") == 0 && i + 1 < argc) {
            g_app.width = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--height") == 0 && i + 1 < argc) {
            g_app.height = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--decorated") == 0) {
            g_app.decorated = true;
        } else if (strcmp(argv[i], "--devtools") == 0) {
            g_app.dev_tools = true;
        } else if (argv[i][0] != '-') {
            snprintf(g_app.url, sizeof(g_app.url), "%s", argv[i]);
        }
    }

    gdk_set_program_class(g_app.app_id);
    gtk_init(&argc, &argv);

    GtkWidget *win = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    g_app.win = GTK_WINDOW(win);
    gtk_window_set_title(g_app.win, g_app.title);
    gtk_window_set_default_size(g_app.win, g_app.width, g_app.height);
    gtk_window_set_decorated(g_app.win, g_app.decorated);

    WebKitWebContext *context = NULL;
    if (g_app.data_dir[0]) {
        char data_path[1200], cache_path[1200];
        snprintf(data_path, sizeof(data_path), "%s/data", g_app.data_dir);
        snprintf(cache_path, sizeof(cache_path), "%s/cache", g_app.data_dir);
        WebKitWebsiteDataManager *dm = webkit_website_data_manager_new(
            "base-data-directory", data_path,
            "base-cache-directory", cache_path,
            NULL);
        context = webkit_web_context_new_with_website_data_manager(dm);
        g_object_unref(dm);
    } else {
        context = webkit_web_context_get_default();
    }

    WebKitUserContentManager *ucm = webkit_user_content_manager_new();
    webkit_user_content_manager_register_script_message_handler(ucm, "hostWindow");
    g_signal_connect(ucm, "script-message-received::hostWindow", G_CALLBACK(on_script_message), NULL);

    WebKitWebView *web = WEBKIT_WEB_VIEW(g_object_new(WEBKIT_TYPE_WEB_VIEW,
        "web-context", context,
        "user-content-manager", ucm,
        NULL));
    g_app.web = web;

    WebKitSettings *settings = webkit_web_view_get_settings(web);
    webkit_settings_set_enable_developer_extras(settings, g_app.dev_tools);
    webkit_settings_set_javascript_can_access_clipboard(settings, TRUE);
    webkit_settings_set_enable_smooth_scrolling(settings, TRUE);

    GdkRGBA bg = { 0.086, 0.094, 0.110, 1.0 };
    webkit_web_view_set_background_color(web, &bg);

    g_signal_connect(web, "decide-policy", G_CALLBACK(on_decide_policy), NULL);
    g_signal_connect(web, "context-menu", G_CALLBACK(on_context_menu), NULL);
    g_signal_connect(win, "destroy", G_CALLBACK(gtk_main_quit), NULL);

    gtk_container_add(GTK_CONTAINER(win), GTK_WIDGET(web));
    webkit_web_view_load_uri(web, g_app.url);
    gtk_widget_show_all(win);

    GIOChannel *ch = g_io_channel_unix_new(fileno(stdin));
    g_io_channel_set_encoding(ch, NULL, NULL);
    g_io_channel_set_flags(ch, G_IO_FLAG_NONBLOCK, NULL);
    g_io_add_watch(ch, G_IO_IN | G_IO_HUP | G_IO_ERR, on_stdin_ready, NULL);
    g_io_channel_unref(ch);

    gtk_main();
    return 0;
}
