#include "redirect_map.h"
#include "trace.h"

#include <MinHook.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <ctime>
// Avoid winhttp.h + wininet.h together (header conflict). Types only.
using HINTERNET = void*;
using INTERNET_PORT = WORD;

#pragma comment(lib, "ws2_32.lib")

static RedirectTable g_table;
static HMODULE g_self = nullptr;
static bool g_dnsHooksInstalled = false;
static bool g_extraHooksInstalled = false;
static HANDLE g_worker = nullptr;
static volatile LONG g_stopWorker = 0;

using GetAddrInfoWFn = INT(WINAPI*)(PCWSTR, PCWSTR, const ADDRINFOW*, PADDRINFOW*);
using GetAddrInfoExWFn = INT(WINAPI*)(PCWSTR, PCWSTR, DWORD, LPGUID, const ADDRINFOEXW*, PADDRINFOEXW*, time_t*, LPOVERLAPPED, LPLOOKUPSERVICE_COMPLETION_ROUTINE, LPHANDLE);
using GetAddrInfoFn = INT(WINAPI*)(PCSTR, PCSTR, const ADDRINFOA*, PADDRINFOA*);
using GethostbynameFn = hostent*(WINAPI*)(const char*);
using ConnectFn = int(WSAAPI*)(SOCKET, const sockaddr*, int);
using WSAConnectFn = int(WSAAPI*)(SOCKET, const sockaddr*, int, LPWSABUF, LPWSABUF, LPQOS, LPQOS);
using LoadLibraryExWFn = HMODULE(WINAPI*)(LPCWSTR, HANDLE, DWORD);
using WinHttpConnectFn = HINTERNET(WINAPI*)(HINTERNET, LPCWSTR, INTERNET_PORT, DWORD);
using WinHttpOpenRequestFn = HINTERNET(WINAPI*)(HINTERNET, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR*, DWORD);
using InternetConnectWFn = HINTERNET(WINAPI*)(HINTERNET, LPCWSTR, INTERNET_PORT, LPCWSTR, LPCWSTR, DWORD, DWORD, DWORD_PTR);
using HttpOpenRequestWFn = HINTERNET(WINAPI*)(HINTERNET, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR*, DWORD, DWORD_PTR);
using SteamInitFn = bool(__cdecl*)();
using SteamGetAuthFn = unsigned int(__cdecl*)(void*, void*, int, unsigned int*);
using SteamGetAuthWebFn = unsigned int(__cdecl*)(void*, const char*);

static GetAddrInfoWFn Real_GetAddrInfoW = nullptr;
static GetAddrInfoExWFn Real_GetAddrInfoExW = nullptr;
static GetAddrInfoFn Real_getaddrinfo = nullptr;
static GethostbynameFn Real_gethostbyname = nullptr;
static ConnectFn Real_connect = nullptr;
static WSAConnectFn Real_WSAConnect = nullptr;
static LoadLibraryExWFn Real_LoadLibraryExW = nullptr;
static WinHttpConnectFn Real_WinHttpConnect = nullptr;
static WinHttpOpenRequestFn Real_WinHttpOpenRequest = nullptr;
static InternetConnectWFn Real_InternetConnectW = nullptr;
static HttpOpenRequestWFn Real_HttpOpenRequestW = nullptr;
static SteamInitFn Real_SteamAPI_Init = nullptr;
static SteamGetAuthFn Real_SteamAPI_ISteamUser_GetAuthSessionTicket = nullptr;
static SteamGetAuthWebFn Real_SteamAPI_ISteamUser_GetAuthTicketForWebApi = nullptr;

static INT MakeAddrInfoW(uint32_t ipv4NetworkOrder, PCWSTR canonicalName, const ADDRINFOW* hints, PADDRINFOW* result)
{
    auto* info = static_cast<ADDRINFOW*>(calloc(1, sizeof(ADDRINFOW)));
    auto* addr = static_cast<sockaddr_in*>(calloc(1, sizeof(sockaddr_in)));
    if (!info || !addr)
    {
        free(info);
        free(addr);
        return WSANO_RECOVERY;
    }
    info->ai_family = hints && hints->ai_family != AF_UNSPEC ? hints->ai_family : AF_INET;
    if (info->ai_family != AF_INET)
    {
        free(info);
        free(addr);
        return WSATYPE_NOT_FOUND;
    }
    info->ai_socktype = hints ? hints->ai_socktype : SOCK_STREAM;
    info->ai_protocol = hints ? hints->ai_protocol : IPPROTO_TCP;
    info->ai_addrlen = sizeof(sockaddr_in);
    info->ai_addr = reinterpret_cast<sockaddr*>(addr);
    addr->sin_family = AF_INET;
    addr->sin_addr.S_un.S_addr = ipv4NetworkOrder;
    if (canonicalName)
    {
        const size_t bytes = (wcslen(canonicalName) + 1) * sizeof(wchar_t);
        info->ai_canonname = static_cast<PWSTR>(malloc(bytes));
        if (info->ai_canonname) memcpy(info->ai_canonname, canonicalName, bytes);
    }
    *result = info;
    return 0;
}

static INT MakeAddrInfoExW(uint32_t ipv4NetworkOrder, PCWSTR canonicalName, const ADDRINFOEXW* hints, PADDRINFOEXW* result)
{
    auto* info = static_cast<ADDRINFOEXW*>(calloc(1, sizeof(ADDRINFOEXW)));
    auto* addr = static_cast<sockaddr_in*>(calloc(1, sizeof(sockaddr_in)));
    if (!info || !addr)
    {
        free(info);
        free(addr);
        return WSANO_RECOVERY;
    }
    info->ai_family = hints && hints->ai_family != AF_UNSPEC ? hints->ai_family : AF_INET;
    if (info->ai_family != AF_INET)
    {
        free(info);
        free(addr);
        return WSATYPE_NOT_FOUND;
    }
    info->ai_socktype = hints ? hints->ai_socktype : SOCK_STREAM;
    info->ai_protocol = hints ? hints->ai_protocol : IPPROTO_TCP;
    info->ai_addrlen = sizeof(sockaddr_in);
    info->ai_addr = reinterpret_cast<sockaddr*>(addr);
    addr->sin_family = AF_INET;
    addr->sin_addr.S_un.S_addr = ipv4NetworkOrder;
    *result = info;
    return 0;
}

static INT MakeAddrInfoA(uint32_t ipv4NetworkOrder, PCSTR canonicalName, const ADDRINFOA* hints, PADDRINFOA* result)
{
    auto* info = static_cast<ADDRINFOA*>(calloc(1, sizeof(ADDRINFOA)));
    auto* addr = static_cast<sockaddr_in*>(calloc(1, sizeof(sockaddr_in)));
    if (!info || !addr)
    {
        free(info);
        free(addr);
        return WSANO_RECOVERY;
    }
    info->ai_family = hints && hints->ai_family != AF_UNSPEC ? hints->ai_family : AF_INET;
    if (info->ai_family != AF_INET)
    {
        free(info);
        free(addr);
        return WSATYPE_NOT_FOUND;
    }
    info->ai_socktype = hints ? hints->ai_socktype : SOCK_STREAM;
    info->ai_protocol = hints ? hints->ai_protocol : IPPROTO_TCP;
    info->ai_addrlen = sizeof(sockaddr_in);
    info->ai_addr = reinterpret_cast<sockaddr*>(addr);
    addr->sin_family = AF_INET;
    addr->sin_addr.S_un.S_addr = ipv4NetworkOrder;
    if (canonicalName)
    {
        const size_t bytes = strlen(canonicalName) + 1;
        info->ai_canonname = static_cast<PSTR>(malloc(bytes));
        if (info->ai_canonname) memcpy(info->ai_canonname, canonicalName, bytes);
    }
    *result = info;
    return 0;
}

static hostent* MakeHostent(uint32_t ipv4NetworkOrder, const char* name)
{
    thread_local static char canonical[256];
    thread_local static char* aliases[1] = {nullptr};
    thread_local static in_addr addr;
    thread_local static char* addresses[2];
    thread_local static hostent entry;

    strncpy_s(canonical, name, _TRUNCATE);
    addr.S_un.S_addr = ipv4NetworkOrder;
    addresses[0] = reinterpret_cast<char*>(&addr);
    addresses[1] = nullptr;
    entry.h_name = canonical;
    entry.h_aliases = aliases;
    entry.h_addrtype = AF_INET;
    entry.h_length = static_cast<int>(sizeof(in_addr));
    entry.h_addr_list = addresses;
    return &entry;
}

static INT WINAPI Hook_GetAddrInfoW(PCWSTR node, PCWSTR service, const ADDRINFOW* hints, PADDRINFOW* result)
{
    uint32_t ip = 0;
    const bool redirect = node && g_table.ShouldRedirect(node, &ip);
    char name[512];
    TraceNarrow(node, name, sizeof(name));
    char svc[128];
    TraceNarrow(service, svc, sizeof(svc));
    if (redirect)
    {
        char ipText[32];
        TraceIpv4(ip, ipText, sizeof(ipText));
        TraceLog("GetAddrInfoW name=%s service=%s redirected=1 ip=%s", name, svc, ipText);
        return MakeAddrInfoW(ip, node, hints, result);
    }
    TraceLog("GetAddrInfoW name=%s service=%s redirected=0", name, svc);
    return Real_GetAddrInfoW(node, service, hints, result);
}

static INT WINAPI Hook_GetAddrInfoExW(PCWSTR name, PCWSTR service, DWORD namespaceId, LPGUID provider, const ADDRINFOEXW* hints,
    PADDRINFOEXW* result, time_t* timeout, LPOVERLAPPED overlapped, LPLOOKUPSERVICE_COMPLETION_ROUTINE completion, LPHANDLE nameHandle)
{
    uint32_t ip = 0;
    const bool canRedirect = name && overlapped == nullptr && completion == nullptr && g_table.ShouldRedirect(name, &ip);
    char host[512];
    TraceNarrow(name, host, sizeof(host));
    if (canRedirect)
    {
        char ipText[32];
        TraceIpv4(ip, ipText, sizeof(ipText));
        TraceLog("GetAddrInfoExW name=%s redirected=1 ip=%s", host, ipText);
        return MakeAddrInfoExW(ip, name, hints, result);
    }
    TraceLog("GetAddrInfoExW name=%s redirected=0 async=%d", host, (overlapped || completion) ? 1 : 0);
    return Real_GetAddrInfoExW(name, service, namespaceId, provider, hints, result, timeout, overlapped, completion, nameHandle);
}

static INT WINAPI Hook_getaddrinfo(PCSTR node, PCSTR service, const ADDRINFOA* hints, PADDRINFOA* result)
{
    uint32_t ip = 0;
    const bool redirect = node && g_table.ShouldRedirectAscii(node, &ip);
    if (redirect)
    {
        char ipText[32];
        TraceIpv4(ip, ipText, sizeof(ipText));
        TraceLog("getaddrinfo name=%s service=%s redirected=1 ip=%s", node ? node : "", service ? service : "", ipText);
        return MakeAddrInfoA(ip, node, hints, result);
    }
    TraceLog("getaddrinfo name=%s service=%s redirected=0", node ? node : "", service ? service : "");
    return Real_getaddrinfo(node, service, hints, result);
}

static hostent* WINAPI Hook_gethostbyname(const char* name)
{
    uint32_t ip = 0;
    const bool redirect = name && g_table.ShouldRedirectAscii(name, &ip);
    if (redirect)
    {
        char ipText[32];
        TraceIpv4(ip, ipText, sizeof(ipText));
        TraceLog("gethostbyname name=%s redirected=1 ip=%s", name, ipText);
        return MakeHostent(ip, name);
    }
    TraceLog("gethostbyname name=%s redirected=0", name ? name : "");
    return Real_gethostbyname(name);
}

static int WSAAPI Hook_connect(SOCKET s, const sockaddr* name, int namelen)
{
    char dest[256];
    TraceSockaddr(name, namelen, dest, sizeof(dest));
    TraceLog("connect %s", dest);
    return Real_connect(s, name, namelen);
}

static int WSAAPI Hook_WSAConnect(SOCKET s, const sockaddr* name, int namelen, LPWSABUF callerData, LPWSABUF calleeData, LPQOS sqos, LPQOS gqos)
{
    char dest[256];
    TraceSockaddr(name, namelen, dest, sizeof(dest));
    TraceLog("WSAConnect %s", dest);
    return Real_WSAConnect(s, name, namelen, callerData, calleeData, sqos, gqos);
}

static HMODULE WINAPI Hook_LoadLibraryExW(LPCWSTR fileName, HANDLE file, DWORD flags)
{
    char name[512];
    TraceNarrow(fileName, name, sizeof(name));
    TraceLog("LoadLibraryExW %s flags=0x%lx", name, static_cast<unsigned long>(flags));
    return Real_LoadLibraryExW(fileName, file, flags);
}

static HINTERNET WINAPI Hook_WinHttpConnect(HINTERNET session, LPCWSTR serverName, INTERNET_PORT port, DWORD reserved)
{
    char host[512];
    TraceNarrow(serverName, host, sizeof(host));
    TraceLog("WinHttpConnect host=%s port=%u", host, static_cast<unsigned>(port));
    return Real_WinHttpConnect(session, serverName, port, reserved);
}

static HINTERNET WINAPI Hook_WinHttpOpenRequest(HINTERNET connect, LPCWSTR verb, LPCWSTR objectName, LPCWSTR version, LPCWSTR referrer, LPCWSTR* acceptTypes, DWORD flags)
{
    char v[64], path[1024];
    TraceNarrow(verb, v, sizeof(v));
    TraceNarrow(objectName, path, sizeof(path));
    TraceLog("WinHttpOpenRequest %s %s flags=0x%lx", v, path, static_cast<unsigned long>(flags));
    return Real_WinHttpOpenRequest(connect, verb, objectName, version, referrer, acceptTypes, flags);
}

static HINTERNET WINAPI Hook_InternetConnectW(HINTERNET session, LPCWSTR serverName, INTERNET_PORT port, LPCWSTR user, LPCWSTR pass, DWORD service, DWORD flags, DWORD_PTR context)
{
    char host[512];
    TraceNarrow(serverName, host, sizeof(host));
    TraceLog("InternetConnectW host=%s port=%u service=%lu", host, static_cast<unsigned>(port), static_cast<unsigned long>(service));
    return Real_InternetConnectW(session, serverName, port, user, pass, service, flags, context);
}

static HINTERNET WINAPI Hook_HttpOpenRequestW(HINTERNET connect, LPCWSTR verb, LPCWSTR objectName, LPCWSTR version, LPCWSTR referrer, LPCWSTR* acceptTypes, DWORD flags, DWORD_PTR context)
{
    char v[64], path[1024];
    TraceNarrow(verb, v, sizeof(v));
    TraceNarrow(objectName, path, sizeof(path));
    TraceLog("HttpOpenRequestW %s %s flags=0x%lx", v, path, static_cast<unsigned long>(flags));
    return Real_HttpOpenRequestW(connect, verb, objectName, version, referrer, acceptTypes, flags, context);
}

static bool __cdecl Hook_SteamAPI_Init()
{
    TraceLog("SteamAPI_Init");
    return Real_SteamAPI_Init();
}

static unsigned int __cdecl Hook_SteamAPI_ISteamUser_GetAuthSessionTicket(void* user, void* ticket, int maxTicket, unsigned int* outLen)
{
    TraceLog("SteamAPI_ISteamUser_GetAuthSessionTicket max=%d", maxTicket);
    return Real_SteamAPI_ISteamUser_GetAuthSessionTicket(user, ticket, maxTicket, outLen);
}

static unsigned int __cdecl Hook_SteamAPI_ISteamUser_GetAuthTicketForWebApi(void* user, const char* identity)
{
    TraceLog("SteamAPI_ISteamUser_GetAuthTicketForWebApi identity=%s", identity ? identity : "");
    return Real_SteamAPI_ISteamUser_GetAuthTicketForWebApi(user, identity);
}

static bool HookOne(LPCWSTR module, LPCSTR name, LPVOID detour, LPVOID* original)
{
    const MH_STATUS st = MH_CreateHookApi(module, name, detour, original);
    if (st != MH_OK)
    {
        char narrow[128];
        TraceNarrow(module, narrow, sizeof(narrow));
        TraceLog("hook failed %s!%s status=%d", narrow, name, static_cast<int>(st));
        return false;
    }
    return true;
}

static bool InstallDnsHooks()
{
    if (MH_Initialize() != MH_OK)
    {
        TraceLog("MH_Initialize failed");
        return false;
    }
    bool ok = true;
    ok = HookOne(L"ws2_32", "GetAddrInfoW", reinterpret_cast<LPVOID>(&Hook_GetAddrInfoW), reinterpret_cast<LPVOID*>(&Real_GetAddrInfoW)) && ok;
    ok = HookOne(L"ws2_32", "GetAddrInfoExW", reinterpret_cast<LPVOID>(&Hook_GetAddrInfoExW), reinterpret_cast<LPVOID*>(&Real_GetAddrInfoExW)) && ok;
    ok = HookOne(L"ws2_32", "getaddrinfo", reinterpret_cast<LPVOID>(&Hook_getaddrinfo), reinterpret_cast<LPVOID*>(&Real_getaddrinfo)) && ok;
    ok = HookOne(L"ws2_32", "gethostbyname", reinterpret_cast<LPVOID>(&Hook_gethostbyname), reinterpret_cast<LPVOID*>(&Real_gethostbyname)) && ok;
    if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
    {
        TraceLog("MH_EnableHook(dns) failed");
        return false;
    }
    return ok;
}

static bool HookIfLoaded(LPCWSTR module, LPCSTR name, LPVOID detour, LPVOID* original)
{
    if (*original) return true;
    return HookOne(module, name, detour, original);
}

static void InstallExtraHooks()
{
    HookIfLoaded(L"ws2_32", "connect", reinterpret_cast<LPVOID>(&Hook_connect), reinterpret_cast<LPVOID*>(&Real_connect));
    HookIfLoaded(L"ws2_32", "WSAConnect", reinterpret_cast<LPVOID>(&Hook_WSAConnect), reinterpret_cast<LPVOID*>(&Real_WSAConnect));
    HookIfLoaded(L"kernel32", "LoadLibraryExW", reinterpret_cast<LPVOID>(&Hook_LoadLibraryExW), reinterpret_cast<LPVOID*>(&Real_LoadLibraryExW));

    HMODULE winhttp = GetModuleHandleW(L"winhttp.dll");
    if (!winhttp) winhttp = LoadLibraryW(L"winhttp.dll");
    if (winhttp)
    {
        HookIfLoaded(L"winhttp", "WinHttpConnect", reinterpret_cast<LPVOID>(&Hook_WinHttpConnect), reinterpret_cast<LPVOID*>(&Real_WinHttpConnect));
        HookIfLoaded(L"winhttp", "WinHttpOpenRequest", reinterpret_cast<LPVOID>(&Hook_WinHttpOpenRequest), reinterpret_cast<LPVOID*>(&Real_WinHttpOpenRequest));
        TraceLog("winhttp hooks ready");
    }
    else TraceLog("winhttp.dll not loaded (%lu)", GetLastError());

    HMODULE wininet = GetModuleHandleW(L"wininet.dll");
    if (!wininet) wininet = LoadLibraryW(L"wininet.dll");
    if (wininet)
    {
        HookIfLoaded(L"wininet", "InternetConnectW", reinterpret_cast<LPVOID>(&Hook_InternetConnectW), reinterpret_cast<LPVOID*>(&Real_InternetConnectW));
        HookIfLoaded(L"wininet", "HttpOpenRequestW", reinterpret_cast<LPVOID>(&Hook_HttpOpenRequestW), reinterpret_cast<LPVOID*>(&Real_HttpOpenRequestW));
        TraceLog("wininet hooks ready");
    }
    else TraceLog("wininet.dll not loaded (%lu)", GetLastError());

    if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
        TraceLog("MH_EnableHook(extra) failed");
    else
        g_extraHooksInstalled = true;
}

static void HookSteamExports(HMODULE steam)
{
    if (!steam) return;
    auto hookExport = [steam](LPCSTR name, LPVOID detour, LPVOID* original) {
        if (*original) return;
        LPVOID target = reinterpret_cast<LPVOID>(GetProcAddress(steam, name));
        if (!target)
        {
            TraceLog("steam export missing %s", name);
            return;
        }
        const MH_STATUS st = MH_CreateHook(target, detour, original);
        if (st != MH_OK)
        {
            TraceLog("steam hook failed %s status=%d", name, static_cast<int>(st));
            return;
        }
        if (MH_EnableHook(target) != MH_OK)
            TraceLog("steam enable failed %s", name);
        else
            TraceLog("steam hook ready %s", name);
    };
    hookExport("SteamAPI_Init", reinterpret_cast<LPVOID>(&Hook_SteamAPI_Init), reinterpret_cast<LPVOID*>(&Real_SteamAPI_Init));
    hookExport("SteamAPI_ISteamUser_GetAuthSessionTicket",
        reinterpret_cast<LPVOID>(&Hook_SteamAPI_ISteamUser_GetAuthSessionTicket),
        reinterpret_cast<LPVOID*>(&Real_SteamAPI_ISteamUser_GetAuthSessionTicket));
    hookExport("SteamAPI_ISteamUser_GetAuthTicketForWebApi",
        reinterpret_cast<LPVOID>(&Hook_SteamAPI_ISteamUser_GetAuthTicketForWebApi),
        reinterpret_cast<LPVOID*>(&Real_SteamAPI_ISteamUser_GetAuthTicketForWebApi));
}

static DWORD WINAPI WorkerThread(LPVOID)
{
    // Leave DllMain / loader lock before LoadLibrary or further hooking.
    Sleep(1);
    TraceLog("worker start");
    InstallExtraHooks();

    const DWORD start = GetTickCount();
    while (!InterlockedCompareExchange(&g_stopWorker, 0, 0) && GetTickCount() - start < 60000)
    {
        HMODULE steam = GetModuleHandleW(L"steam_api64.dll");
        if (steam)
        {
            TraceLog("steam_api64.dll present");
            HookSteamExports(steam);
            break;
        }
        Sleep(200);
    }
    if (!Real_SteamAPI_Init && !InterlockedCompareExchange(&g_stopWorker, 0, 0))
        TraceLog("steam_api64.dll not loaded within 60s");
    TraceLog("worker done");
    return 0;
}

extern "C" BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        g_self = module;
        TraceInit();
        TraceLog("A2Core.Redirect attached");
        if (!g_table.LoadFromEnvironment()) g_table.LoadFromFileNextToModule(module);
        TraceLog("redirect map entries=%zu", g_table.hostToIpv4.size());
        // Install DNS hooks even when the map is empty so trace.log still records lookups.
        g_dnsHooksInstalled = InstallDnsHooks();
        TraceLog("dns hooks installed=%d", g_dnsHooksInstalled ? 1 : 0);
        g_worker = CreateThread(nullptr, 0, &WorkerThread, nullptr, 0, nullptr);
        if (!g_worker) TraceLog("worker thread failed (%lu)", GetLastError());
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        InterlockedExchange(&g_stopWorker, 1);
        if (g_worker)
        {
            WaitForSingleObject(g_worker, 1000);
            CloseHandle(g_worker);
            g_worker = nullptr;
        }
        if (g_dnsHooksInstalled || g_extraHooksInstalled)
        {
            MH_DisableHook(MH_ALL_HOOKS);
            MH_Uninitialize();
        }
        TraceLog("A2Core.Redirect detached");
        TraceShutdown();
    }
    return TRUE;
}
