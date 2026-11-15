#include "trace.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>

namespace
{
    CRITICAL_SECTION g_cs;
    HANDLE g_file = INVALID_HANDLE_VALUE;
    volatile LONG g_state = 0; // 0=uninit, 1=initing, 2=ready, 3=dead

    void EnsureInit()
    {
        const LONG seen = InterlockedCompareExchange(&g_state, 1, 0);
        if (seen == 2) return;
        if (seen == 3) return;
        if (seen == 1)
        {
            // Another thread is initializing. Don't wait long in a hook; drop the line.
            for (int i = 0; i < 50 && InterlockedCompareExchange(&g_state, 2, 2) != 2; i++)
                Sleep(1);
            return;
        }

        InitializeCriticalSection(&g_cs);

        wchar_t dir[MAX_PATH]{};
        DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", dir, MAX_PATH);
        if (n == 0 || n >= MAX_PATH)
        {
            InterlockedExchange(&g_state, 3);
            return;
        }
        wchar_t folder[MAX_PATH]{};
        if (swprintf_s(folder, L"%s\\A2CorePrivate", dir) < 0)
        {
            InterlockedExchange(&g_state, 3);
            return;
        }
        CreateDirectoryW(folder, nullptr);
        wchar_t path[MAX_PATH]{};
        if (swprintf_s(path, L"%s\\trace.log", folder) < 0)
        {
            InterlockedExchange(&g_state, 3);
            return;
        }
        g_file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (g_file == INVALID_HANDLE_VALUE)
        {
            InterlockedExchange(&g_state, 3);
            return;
        }
        InterlockedExchange(&g_state, 2);
    }

    void WriteLine(const char* text)
    {
        if (!text || g_file == INVALID_HANDLE_VALUE) return;
        EnterCriticalSection(&g_cs);
        SYSTEMTIME st{};
        GetLocalTime(&st);
        char header[80];
        const int headerLen = sprintf_s(header, "%04u-%02u-%02uT%02u:%02u:%02u.%03u tid=%lu ",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
            GetCurrentThreadId());
        if (headerLen > 0)
        {
            DWORD written = 0;
            WriteFile(g_file, header, static_cast<DWORD>(headerLen), &written, nullptr);
            const size_t len = strlen(text);
            if (len > 0) WriteFile(g_file, text, static_cast<DWORD>(len), &written, nullptr);
            WriteFile(g_file, "\r\n", 2, &written, nullptr);
            FlushFileBuffers(g_file);
        }
        LeaveCriticalSection(&g_cs);
    }
}

void TraceInit()
{
    EnsureInit();
}

void TraceShutdown()
{
    if (InterlockedCompareExchange(&g_state, 3, 2) != 2) return;
    EnterCriticalSection(&g_cs);
    if (g_file != INVALID_HANDLE_VALUE)
    {
        CloseHandle(g_file);
        g_file = INVALID_HANDLE_VALUE;
    }
    LeaveCriticalSection(&g_cs);
    DeleteCriticalSection(&g_cs);
}

void TraceLog(const char* fmt, ...)
{
    EnsureInit();
    if (InterlockedCompareExchange(&g_state, 2, 2) != 2) return;
    char body[2048];
    va_list args;
    va_start(args, fmt);
    const int n = vsprintf_s(body, fmt, args);
    va_end(args);
    if (n > 0) WriteLine(body);
}

void TraceLogW(const wchar_t* fmt, ...)
{
    EnsureInit();
    if (InterlockedCompareExchange(&g_state, 2, 2) != 2) return;
    wchar_t wide[1024];
    va_list args;
    va_start(args, fmt);
    const int n = vswprintf_s(wide, fmt, args);
    va_end(args);
    if (n <= 0) return;
    char body[2048];
    TraceNarrow(wide, body, static_cast<int>(sizeof(body)));
    WriteLine(body);
}

void TraceNarrow(const wchar_t* wide, char* out, int outBytes)
{
    if (!out || outBytes <= 0) return;
    out[0] = '\0';
    if (!wide) return;
    const int n = WideCharToMultiByte(CP_UTF8, 0, wide, -1, out, outBytes, nullptr, nullptr);
    if (n <= 0)
    {
        out[0] = '?';
        if (outBytes > 1) out[1] = '\0';
    }
}

void TraceIpv4(unsigned int ipv4NetworkOrder, char* out, int outBytes)
{
    if (!out || outBytes <= 0) return;
    const unsigned int a = ipv4NetworkOrder & 0xFFu;
    const unsigned int b = (ipv4NetworkOrder >> 8) & 0xFFu;
    const unsigned int c = (ipv4NetworkOrder >> 16) & 0xFFu;
    const unsigned int d = (ipv4NetworkOrder >> 24) & 0xFFu;
    sprintf_s(out, static_cast<size_t>(outBytes), "%u.%u.%u.%u", a, b, c, d);
}

void TraceSockaddr(const struct sockaddr* addr, int addrLen, char* out, int outBytes)
{
    if (!out || outBytes <= 0) return;
    out[0] = '\0';
    if (!addr || addrLen < static_cast<int>(sizeof(sockaddr)))
    {
        sprintf_s(out, static_cast<size_t>(outBytes), "invalid");
        return;
    }
    if (addr->sa_family == AF_INET && addrLen >= static_cast<int>(sizeof(sockaddr_in)))
    {
        const auto* v4 = reinterpret_cast<const sockaddr_in*>(addr);
        char ip[32];
        TraceIpv4(v4->sin_addr.S_un.S_addr, ip, sizeof(ip));
        sprintf_s(out, static_cast<size_t>(outBytes), "%s:%u", ip, ntohs(v4->sin_port));
        return;
    }
    if (addr->sa_family == AF_INET6 && addrLen >= static_cast<int>(sizeof(sockaddr_in6)))
    {
        const auto* v6 = reinterpret_cast<const sockaddr_in6*>(addr);
        char ip[128]{};
        if (InetNtopA(AF_INET6, &v6->sin6_addr, ip, sizeof(ip)))
            sprintf_s(out, static_cast<size_t>(outBytes), "[%s]:%u", ip, ntohs(v6->sin6_port));
        else
            sprintf_s(out, static_cast<size_t>(outBytes), "inet6:%u", ntohs(v6->sin6_port));
        return;
    }
    sprintf_s(out, static_cast<size_t>(outBytes), "family=%u", static_cast<unsigned>(addr->sa_family));
}
