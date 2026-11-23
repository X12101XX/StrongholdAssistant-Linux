CC ?= gcc
CFLAGS ?= -O2 -Wall -Wextra -fPIC
GEN_DIR := gen

PROTO_XMLS := \
	protocols/wlr-screencopy-unstable-v1.xml \
	protocols/wlr-virtual-pointer-unstable-v1.xml \
	protocols/virtual-keyboard-unstable-v1.xml

GEN_HDRS := \
	$(GEN_DIR)/wlr-screencopy-unstable-v1-client-protocol.h \
	$(GEN_DIR)/wlr-virtual-pointer-unstable-v1-client-protocol.h \
	$(GEN_DIR)/virtual-keyboard-unstable-v1-client-protocol.h

GEN_SRCS := \
	$(GEN_DIR)/wlr-screencopy-unstable-v1-protocol.c \
	$(GEN_DIR)/wlr-virtual-pointer-unstable-v1-protocol.c \
	$(GEN_DIR)/virtual-keyboard-unstable-v1-protocol.c

.PHONY: all clean wayland overlay webview

all: wayland

wayland: libstronghold_wayland.so

$(GEN_DIR):
	mkdir -p $(GEN_DIR)

$(GEN_DIR)/%-client-protocol.h: protocols/%.xml | $(GEN_DIR)
	wayland-scanner client-header $< $@

$(GEN_DIR)/%-protocol.c: protocols/%.xml | $(GEN_DIR)
	wayland-scanner private-code $< $@

libstronghold_wayland.so: stronghold_wayland.c $(GEN_HDRS) $(GEN_SRCS)
	$(CC) $(CFLAGS) -shared -I$(GEN_DIR) -o $@ stronghold_wayland.c $(GEN_SRCS) -lwayland-client -lpthread

stronghold-overlay: stronghold_overlay.c
	$(CC) -O2 -Wall -Wextra -o $@ $< $$(pkg-config --cflags --libs gtk4 gtk4-layer-shell-0 cairo) -lm

stronghold-webview: stronghold_webview.c
	$(CC) -O2 -Wall -Wextra -o $@ $< $$(pkg-config --cflags --libs gtk+-3.0 webkit2gtk-4.1)

clean:
	rm -rf $(GEN_DIR) libstronghold_wayland.so stronghold-overlay stronghold-webview
