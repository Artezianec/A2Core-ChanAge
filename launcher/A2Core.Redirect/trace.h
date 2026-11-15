#pragma once

#include <windows.h>

// Best-effort append-only tracer. Never throws, never changes game behaviour.
void TraceInit();
void TraceShutdown();
void TraceLog(const char* fmt, ...);
void TraceLogW(const wchar_t* fmt, ...);

// UTF-8 helpers for hook log lines. Out buffers are always NUL-terminated.
void TraceNarrow(const wchar_t* wide, char* out, int outBytes);
void TraceIpv4(unsigned int ipv4NetworkOrder, char* out, int outBytes);
void TraceSockaddr(const struct sockaddr* addr, int addrLen, char* out, int outBytes);
