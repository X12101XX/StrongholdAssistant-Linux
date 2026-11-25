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
    WebKitWebsiteDataManager *dm;
    char url[1024];
    char title[256];
    char app_id[128];
    char data_dir[1024];
    char allowed_host[256];
    char hint[512];
    int width;
    int height;
    bool decorated;
    bool dev_tools;
    bool session_mode;
    bool hidden;
    bool open_external_new_window;
    int crash_reloads;
    char *line_buf;
    size_t line_len;
    size_t line_cap;
} app_ctx_t;

static app_ctx_t g_app = {
    .width = 400,
    .height = 900,
    .decorated = false,
    .dev_tools = false,
    .session_mode = false,
    .hidden = false,
    .open_external_new_window = false,
};

// Injected at document-start in --session-mode:
// 1. Provides window.chrome.webview.postMessage(...) -> webkit.messageHandlers.shBridge
// 2. Hooks window.fetch and XMLHttpRequest to mirror zonai.skland.com API responses
static const char SESSION_INJECT_JS[] =
    "(() => {\n"
    "  if (window.__shLinuxSessionHooked) return;\n"
    "  window.__shLinuxSessionHooked = true;\n"
    "  const emit = (obj) => {\n"
    "    try { window.webkit.messageHandlers.shBridge.postMessage(JSON.stringify(obj)); } catch (e) {}\n"
    "  };\n"
    "  window.chrome = window.chrome || {};\n"
    "  if (!window.chrome.webview) {\n"
    "    window.chrome.webview = {\n"
    "      postMessage(msg) {\n"
    "        emit({ type: 'webmsg', url: String(location.href || ''), data: msg });\n"
    "      },\n"
    "      addEventListener() {},\n"
    "      removeEventListener() {}\n"
    "    };\n"
    "  }\n"
    "  const shouldCapture = (u) => {\n"
    "    try {\n"
    "      const parsed = new URL(u, location.href);\n"
    "      return parsed.protocol === 'https:' && parsed.hostname.endsWith('.skland.com');\n"
    "    } catch { return false; }\n"
    "  };\n"
    "  const origFetch = window.fetch;\n"
    "  if (typeof origFetch === 'function') {\n"
    "    window.fetch = function(input, init) {\n"
    "      const reqUrl = typeof input === 'string' ? input : (input && input.url) || '';\n"
    "      const method = ((init && init.method) || (input && input.method) || 'GET').toUpperCase();\n"
    "      const p = origFetch.apply(this, arguments);\n"
    "      if (shouldCapture(reqUrl)) {\n"
    "        p.then((res) => {\n"
    "          try {\n"
    "            const finalUrl = res.url || new URL(reqUrl, location.href).href;\n"
    "            const status = res.status;\n"
    "            res.clone().text().then((body) => {\n"
    "              emit({ type: 'http', method, url: finalUrl, status, body });\n"
    "            }).catch(() => {\n"
    "              emit({ type: 'http', method, url: finalUrl, status, body: null });\n"
    "            });\n"
    "          } catch {}\n"
    "        }).catch(() => {});\n"
    "      }\n"
    "      return p;\n"
    "    };\n"
    "  }\n"
    "  const origOpen = XMLHttpRequest.prototype.open;\n"
    "  const origSend = XMLHttpRequest.prototype.send;\n"
    "  XMLHttpRequest.prototype.open = function(method, url) {\n"
    "    this.__shMethod = String(method || 'GET').toUpperCase();\n"
    "    this.__shUrl = String(url || '');\n"
    "    return origOpen.apply(this, arguments);\n"
    "  };\n"
    "  XMLHttpRequest.prototype.send = function() {\n"
    "    if (this.__shUrl && shouldCapture(this.__shUrl)) {\n"
    "      this.addEventListener('loadend', () => {\n"
    "        try {\n"
    "          const finalUrl = this.responseURL || new URL(this.__shUrl, location.href).href;\n"
    "          let body = null;\n"
    "          if (this.responseType === '' || this.responseType === 'text') body = this.responseText;\n"
    "          else if (this.responseType === 'json' && this.response != null) body = JSON.stringify(this.response);\n"
    "          emit({ type: 'http', method: this.__shMethod || 'GET', url: finalUrl, status: this.status || 0, body });\n"
    "        } catch {}\n"
    "      });\n"
    "    }\n"
    "    return origSend.apply(this, arguments);\n"
    "  };\n"
    "})();\n";

static const char HIDDEN_VISIBILITY_SHIM_JS[] =
    "(() => {\n"
    "  try {\n"
    "    Object.defineProperty(document, 'hidden', { get: () => false, configurable: true });\n"
    "    Object.defineProperty(document, 'visibilityState', { get: () => 'visible', configurable: true });\n"
    "  } catch {}\n"
    "  window.requestAnimationFrame = (cb) => setTimeout(() => cb(performance.now()), 16);\n"
    "  window.cancelAnimationFrame = (id) => clearTimeout(id);\n"
    "})();\n";

static void emit_json_str(const char *s) {
    putchar('"');
    for (; s && *s; s++) {
        unsigned char c = (unsigned char)*s;
        switch (c) {
            case '"': fputs("\\\"", stdout); break;
            case '\\': fputs("\\\\", stdout); break;
            case '\n': fputs("\\n", stdout); break;
            case '\r': fputs("\\r", stdout); break;
            case '\t': fputs("\\t", stdout); break;
            default:
                if (c < 0x20) printf("\\u%04x", c);
                else putchar(c);
        }
    }
    putchar('"');
}

static void emit_raw_line(const char *json) {
    if (!g_app.session_mode || !json) return;
    fputs(json, stdout);
    putchar('\n');
    fflush(stdout);
}

static void emit_load_event(const char *ev, const char *url, const char *err) {
    if (!g_app.session_mode) return;
    fputs("{\"type\":\"load\",\"event\":", stdout);
    emit_json_str(ev);
    fputs(",\"url\":", stdout);
    emit_json_str(url ? url : "");
    if (err) {
        fputs(",\"error\":", stdout);
        emit_json_str(err);
    }
    fputs("}\n", stdout);
    fflush(stdout);
}

static void on_web_process_terminated(WebKitWebView *web, WebKitWebProcessTerminationReason reason, gpointer data) {
    (void)data;
    fprintf(stderr, "stronghold-webview: web process terminated (reason=%d)\n", (int)reason);
    if (!g_app.session_mode && g_app.url[0] && g_app.crash_reloads < 5) {
        g_app.crash_reloads++;
        webkit_web_view_load_uri(web, g_app.url);
    }
}

static void on_host_window_message(WebKitUserContentManager *mgr, WebKitJavascriptResult *res, gpointer data) {
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

static void on_bridge_message(WebKitUserContentManager *mgr, WebKitJavascriptResult *res, gpointer data) {
    (void)mgr; (void)data;
    JSCValue *val = webkit_javascript_result_get_js_value(res);
    if (!val || !jsc_value_is_string(val)) return;
    char *json = jsc_value_to_string(val);
    if (json) {
        emit_raw_line(json);
        g_free(json);
    }
}

static void on_eval_finished(GObject *src, GAsyncResult *res, gpointer user_data) {
    char *req_id = (char *)user_data;
    GError *err = NULL;
    JSCValue *val = webkit_web_view_evaluate_javascript_finish(WEBKIT_WEB_VIEW(src), res, &err);
    fputs("{\"type\":\"eval\",\"id\":", stdout);
    emit_json_str(req_id);
    if (err) {
        fputs(",\"ok\":false,\"error\":", stdout);
        emit_json_str(err->message ? err->message : "eval error");
        g_error_free(err);
    } else if (val) {
        char *json = jsc_value_to_json(val, 0);
        fputs(",\"ok\":true,\"result\":", stdout);
        fputs(json ? json : "null", stdout);
        g_free(json);
        g_object_unref(val);
    } else {
        fputs(",\"ok\":true,\"result\":null", stdout);
    }
    fputs("}\n", stdout);
    fflush(stdout);
    g_free(req_id);
}

static void on_clear_finished(GObject *src, GAsyncResult *res, gpointer user_data) {
    char *req_id = (char *)user_data;
    GError *err = NULL;
    webkit_website_data_manager_clear_finish(WEBKIT_WEBSITE_DATA_MANAGER(src), res, &err);
    fputs("{\"type\":\"cleared\",\"id\":", stdout);
    emit_json_str(req_id);
    if (err) {
        fputs(",\"ok\":false,\"error\":", stdout);
        emit_json_str(err->message ? err->message : "clear error");
        g_error_free(err);
    } else {
        fputs(",\"ok\":true", stdout);
    }
    fputs("}\n", stdout);
    fflush(stdout);
    g_free(req_id);
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
        gtk_widget_show_all(GTK_WIDGET(g_app.win));
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
    } else if (strncmp(line, "RELOAD", 6) == 0) {
        webkit_web_view_reload(g_app.web);
    } else if (strncmp(line, "EVAL ", 5) == 0) {
        const char *p = line + 5;
        const char *sp = strchr(p, ' ');
        if (sp && sp > p) {
            char *req_id = g_strndup(p, (gsize)(sp - p));
            gsize decoded_len = 0;
            guchar *decoded = g_base64_decode(sp + 1, &decoded_len);
            webkit_web_view_evaluate_javascript(g_app.web, (const char *)decoded, (gssize)decoded_len,
                NULL, NULL, NULL, on_eval_finished, req_id);
            g_free(decoded);
        }
    } else if (strncmp(line, "CLEAR_DATA ", 11) == 0) {
        char *req_id = g_strdup(line + 11);
        if (g_app.dm) {
            webkit_website_data_manager_clear(g_app.dm, WEBKIT_WEBSITE_DATA_ALL, 0, NULL, on_clear_finished, req_id);
        } else {
            fputs("{\"type\":\"cleared\",\"id\":", stdout);
            emit_json_str(req_id);
            fputs(",\"ok\":true}\n", stdout);
            fflush(stdout);
            g_free(req_id);
        }
    }
}

static gboolean on_stdin_ready(GIOChannel *source, GIOCondition cond, gpointer data) {
    (void)data;
    if (cond & (G_IO_HUP | G_IO_ERR)) {
        gtk_main_quit();
        return FALSE;
    }
    char buf[4096];
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
            if (g_app.line_buf && g_app.line_len > 0) {
                g_app.line_buf[g_app.line_len] = '\0';
                handle_stdin_cmd(g_app.line_buf);
            }
            g_app.line_len = 0;
        } else if (c != '\r') {
            if (g_app.line_len + 2 >= g_app.line_cap) {
                g_app.line_cap = g_app.line_cap == 0 ? 8192 : g_app.line_cap * 2;
                g_app.line_buf = g_realloc(g_app.line_buf, g_app.line_cap);
            }
            g_app.line_buf[g_app.line_len++] = c;
        }
    }
    return TRUE;
}

static bool uri_matches_allowed_host(const char *uri) {
    if (!g_app.allowed_host[0]) return true;
    if (!uri) return false;
    if (strncmp(uri, "about:", 6) == 0) return true;
    if (strncmp(uri, "https://", 8) != 0) return false;
    const char *host = uri + 8;
    const char *end = host;
    while (*end && *end != '/' && *end != '?' && *end != '#' && *end != ':') end++;
    size_t hlen = (size_t)(end - host);
    return hlen == strlen(g_app.allowed_host) && strncmp(host, g_app.allowed_host, hlen) == 0;
}

static gboolean on_decide_policy(WebKitWebView *web, WebKitPolicyDecision *decision,
    WebKitPolicyDecisionType type, gpointer data) {
    (void)data;
    if (type == WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION) {
        WebKitNavigationPolicyDecision *nav = WEBKIT_NAVIGATION_POLICY_DECISION(decision);
        WebKitNavigationAction *action = webkit_navigation_policy_decision_get_navigation_action(nav);
        WebKitURIRequest *req = webkit_navigation_action_get_request(action);
        const char *uri = webkit_uri_request_get_uri(req);
        if (g_app.allowed_host[0] && !uri_matches_allowed_host(uri)) {
            webkit_policy_decision_ignore(decision);
            return TRUE;
        }
        return FALSE;
    }
    if (type == WEBKIT_POLICY_DECISION_TYPE_NEW_WINDOW_ACTION) {
        WebKitNavigationPolicyDecision *nav = WEBKIT_NAVIGATION_POLICY_DECISION(decision);
        WebKitNavigationAction *action = webkit_navigation_policy_decision_get_navigation_action(nav);
        WebKitURIRequest *req = webkit_navigation_action_get_request(action);
        const char *uri = webkit_uri_request_get_uri(req);
        webkit_policy_decision_ignore(decision);
        if (uri && g_app.open_external_new_window) {
            if (strncmp(uri, "https://", 8) == 0 || strncmp(uri, "http://", 7) == 0) {
                gchar *argv_open[] = { "xdg-open", (gchar *)uri, NULL };
                g_spawn_async(NULL, argv_open, NULL, G_SPAWN_SEARCH_PATH, NULL, NULL, NULL, NULL);
            }
        } else if (uri && g_app.allowed_host[0] && uri_matches_allowed_host(uri)) {
            webkit_web_view_load_uri(web, uri);
        }
        return TRUE;
    }
    return FALSE;
}

static void on_load_changed(WebKitWebView *web, WebKitLoadEvent ev, gpointer data) {
    (void)data;
    if (!g_app.session_mode) return;
    const char *uri = webkit_web_view_get_uri(web);
    if (ev == WEBKIT_LOAD_STARTED) emit_load_event("started", uri, NULL);
    else if (ev == WEBKIT_LOAD_FINISHED) emit_load_event("finished", uri, NULL);
}

static gboolean on_load_failed(WebKitWebView *web, WebKitLoadEvent ev, const gchar *uri, GError *err, gpointer data) {
    (void)web; (void)ev; (void)data;
    if (g_app.session_mode) {
        emit_load_event("failed", uri, err ? err->message : "load failed");
    }
    return FALSE;
}

static gboolean on_context_menu(WebKitWebView *web, WebKitContextMenu *menu,
    GdkEvent *event, WebKitHitTestResult *hit, gpointer data) {
    (void)web; (void)menu; (void)event; (void)hit; (void)data;
    if (g_app.session_mode) return FALSE; // allow copy/paste during login
    return !g_app.dev_tools;
}

static void on_destroy(GtkWidget *w, gpointer data) {
    (void)w; (void)data;
    emit_raw_line("{\"type\":\"closed\"}");
    gtk_main_quit();
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
        } else if (strcmp(argv[i], "--allowed-host") == 0 && i + 1 < argc) {
            snprintf(g_app.allowed_host, sizeof(g_app.allowed_host), "%s", argv[++i]);
        } else if (strcmp(argv[i], "--hint") == 0 && i + 1 < argc) {
            snprintf(g_app.hint, sizeof(g_app.hint), "%s", argv[++i]);
        } else if (strcmp(argv[i], "--width") == 0 && i + 1 < argc) {
            g_app.width = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--height") == 0 && i + 1 < argc) {
            g_app.height = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--decorated") == 0) {
            g_app.decorated = true;
        } else if (strcmp(argv[i], "--devtools") == 0) {
            g_app.dev_tools = true;
        } else if (strcmp(argv[i], "--session-mode") == 0) {
            g_app.session_mode = true;
            g_app.decorated = true;
        } else if (strcmp(argv[i], "--hidden") == 0) {
            g_app.hidden = true;
        } else if (strcmp(argv[i], "--external-new-window") == 0) {
            g_app.open_external_new_window = true;
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
        g_app.dm = dm;
        context = webkit_web_context_new_with_website_data_manager(dm);
        // Persist cookies across runs
        WebKitCookieManager *cm = webkit_website_data_manager_get_cookie_manager(dm);
        char cookie_path[1280];
        snprintf(cookie_path, sizeof(cookie_path), "%s/cookies.sqlite", g_app.data_dir);
        g_mkdir_with_parents(g_app.data_dir, 0700);
        webkit_cookie_manager_set_persistent_storage(cm, cookie_path, WEBKIT_COOKIE_PERSISTENT_STORAGE_SQLITE);
        webkit_cookie_manager_set_accept_policy(cm, WEBKIT_COOKIE_POLICY_ACCEPT_ALWAYS);
    } else {
        context = webkit_web_context_get_default();
        g_app.dm = webkit_web_context_get_website_data_manager(context);
    }

    WebKitUserContentManager *ucm = webkit_user_content_manager_new();
    webkit_user_content_manager_register_script_message_handler(ucm, "hostWindow");
    g_signal_connect(ucm, "script-message-received::hostWindow", G_CALLBACK(on_host_window_message), NULL);

    if (g_app.session_mode) {
        webkit_user_content_manager_register_script_message_handler(ucm, "shBridge");
        g_signal_connect(ucm, "script-message-received::shBridge", G_CALLBACK(on_bridge_message), NULL);
        WebKitUserScript *script = webkit_user_script_new(
            SESSION_INJECT_JS,
            WEBKIT_USER_CONTENT_INJECT_ALL_FRAMES,
            WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START,
            NULL, NULL);
        webkit_user_content_manager_add_script(ucm, script);
        webkit_user_script_unref(script);
        if (g_app.hidden) {
            WebKitUserScript *vis = webkit_user_script_new(
                HIDDEN_VISIBILITY_SHIM_JS,
                WEBKIT_USER_CONTENT_INJECT_ALL_FRAMES,
                WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START,
                NULL, NULL);
            webkit_user_content_manager_add_script(ucm, vis);
            webkit_user_script_unref(vis);
        }
    }

    WebKitWebView *web = WEBKIT_WEB_VIEW(g_object_new(WEBKIT_TYPE_WEB_VIEW,
        "web-context", context,
        "user-content-manager", ucm,
        NULL));
    g_app.web = web;

    WebKitSettings *settings = webkit_web_view_get_settings(web);
    webkit_settings_set_enable_developer_extras(settings, g_app.dev_tools);
    webkit_settings_set_javascript_can_access_clipboard(settings, TRUE);
    webkit_settings_set_enable_smooth_scrolling(settings, TRUE);
    webkit_settings_set_enable_webgl(settings, TRUE);
    webkit_settings_set_enable_back_forward_navigation_gestures(settings, FALSE);
    const char *hw_env = getenv("STRONGHOLD_WEBKIT_HW");
    if (!hw_env || strcmp(hw_env, "0") != 0) {
        webkit_settings_set_hardware_acceleration_policy(settings, WEBKIT_HARDWARE_ACCELERATION_POLICY_ALWAYS);
    } else {
        webkit_settings_set_hardware_acceleration_policy(settings, WEBKIT_HARDWARE_ACCELERATION_POLICY_NEVER);
    }

    if (!g_app.session_mode) {
        GdkRGBA bg = { 0.086, 0.094, 0.110, 1.0 };
        webkit_web_view_set_background_color(web, &bg);
    }

    g_signal_connect(web, "decide-policy", G_CALLBACK(on_decide_policy), NULL);
    g_signal_connect(web, "load-changed", G_CALLBACK(on_load_changed), NULL);
    g_signal_connect(web, "load-failed", G_CALLBACK(on_load_failed), NULL);
    g_signal_connect(web, "web-process-terminated", G_CALLBACK(on_web_process_terminated), NULL);
    g_signal_connect(web, "context-menu", G_CALLBACK(on_context_menu), NULL);
    g_signal_connect(win, "destroy", G_CALLBACK(on_destroy), NULL);

    if (g_app.hint[0]) {
        GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
        GtkWidget *lbl = gtk_label_new(g_app.hint);
        gtk_label_set_line_wrap(GTK_LABEL(lbl), TRUE);
        gtk_widget_set_margin_top(lbl, 8);
        gtk_widget_set_margin_bottom(lbl, 8);
        gtk_widget_set_margin_start(lbl, 12);
        gtk_widget_set_margin_end(lbl, 12);
        gtk_box_pack_start(GTK_BOX(box), lbl, FALSE, FALSE, 0);
        gtk_box_pack_start(GTK_BOX(box), GTK_WIDGET(web), TRUE, TRUE, 0);
        gtk_container_add(GTK_CONTAINER(win), box);
    } else {
        gtk_container_add(GTK_CONTAINER(win), GTK_WIDGET(web));
    }

    if (!g_app.hidden) {
        gtk_widget_show_all(win);
    } else {
        gtk_widget_set_size_request(GTK_WIDGET(web), g_app.width, g_app.height);
        gtk_widget_show(GTK_WIDGET(web));
    }

    if (g_app.url[0] && strcmp(g_app.url, "about:blank") != 0) {
        webkit_web_view_load_uri(web, g_app.url);
    }

    GIOChannel *ch = g_io_channel_unix_new(fileno(stdin));
    g_io_channel_set_encoding(ch, NULL, NULL);
    g_io_channel_set_flags(ch, G_IO_FLAG_NONBLOCK, NULL);
    g_io_add_watch(ch, G_IO_IN | G_IO_HUP | G_IO_ERR, on_stdin_ready, NULL);
    g_io_channel_unref(ch);

    gtk_main();
    g_free(g_app.line_buf);
    return 0;
}
