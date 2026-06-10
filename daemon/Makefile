CC      ?= gcc
CFLAGS  ?= -O2 -Wall -Wextra
PREFIX  ?= $(HOME)/.local

padbridge-daemon: padbridge-daemon.c event-names.h
	$(CC) $(CFLAGS) -o $@ padbridge-daemon.c

event-names.h: ../scripts/gen-event-codes.py
	python3 ../scripts/gen-event-codes.py

install: padbridge-daemon
	install -D -m 755 padbridge-daemon $(PREFIX)/bin/padbridge-daemon

clean:
	rm -f padbridge-daemon

.PHONY: install clean
