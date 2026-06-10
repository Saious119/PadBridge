CC      ?= gcc
CFLAGS  ?= -O2 -Wall -Wextra
PREFIX  ?= $(HOME)/.local

all: padbridge-daemon padbridge-flydigi

padbridge-daemon: padbridge-daemon.c event-names.h
	$(CC) $(CFLAGS) -o $@ padbridge-daemon.c

padbridge-flydigi: padbridge-flydigi.c
	$(CC) $(CFLAGS) -o $@ padbridge-flydigi.c

event-names.h: ../scripts/gen-event-codes.py
	python3 ../scripts/gen-event-codes.py

install: padbridge-daemon padbridge-flydigi
	install -D -m 755 padbridge-daemon $(PREFIX)/bin/padbridge-daemon
	install -D -m 755 padbridge-flydigi $(PREFIX)/bin/padbridge-flydigi

clean:
	rm -f padbridge-daemon padbridge-flydigi

.PHONY: all install clean
