/* Study Harness P1 isolated capability fixture (synthetic, fixed, reviewed).
 *
 * Runs only inside the Study Harness AppContainer. It performs a fixed matrix of
 * probes against synthetic targets chosen by the trusted host and reports raw
 * Win32/Winsock error numbers. It never compiles, executes or interprets anything
 * provided by a model or a learner: every target is a command-line argument that
 * the host created for this run.
 *
 * Exit codes: 0 = matrix completed and report written, non-zero = the fixture
 * itself could not run, which the host records as blocked rather than pass.
 */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

#define REPORT_MAX 2048

static int read_error(const wchar_t *path) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return (int)GetLastError();
    CloseHandle(file);
    return 0;
}

static int write_error(const wchar_t *path, DWORD creation) {
    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, NULL, creation, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return (int)GetLastError();
    CloseHandle(file);
    return 0;
}

static int connect_error(int family, unsigned short port, const wchar_t *address) {
    struct addrinfoW hints, *result = NULL;
    SOCKET sock;
    int status, error;
    wchar_t service[8];
    _snwprintf_s(service, 8, _TRUNCATE, L"%u", (unsigned)port);
    ZeroMemory(&hints, sizeof(hints));
    hints.ai_family = family;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    status = GetAddrInfoW(address, service, &hints, &result);
    if (status != 0) return status;
    sock = socket(result->ai_family, result->ai_socktype, result->ai_protocol);
    if (sock == INVALID_SOCKET) { error = WSAGetLastError(); FreeAddrInfoW(result); return error; }
    error = connect(sock, result->ai_addr, (int)result->ai_addrlen) == 0 ? 0 : WSAGetLastError();
    closesocket(sock);
    FreeAddrInfoW(result);
    return error;
}

static int udp_error(unsigned short port, const wchar_t *address) {
    struct addrinfoW hints, *result = NULL;
    SOCKET sock;
    int status, error, sent;
    wchar_t service[8];
    _snwprintf_s(service, 8, _TRUNCATE, L"%u", (unsigned)port);
    ZeroMemory(&hints, sizeof(hints));
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_DGRAM;
    hints.ai_protocol = IPPROTO_UDP;
    status = GetAddrInfoW(address, service, &hints, &result);
    if (status != 0) return status;
    sock = socket(result->ai_family, result->ai_socktype, result->ai_protocol);
    if (sock == INVALID_SOCKET) { error = WSAGetLastError(); FreeAddrInfoW(result); return error; }
    sent = sendto(sock, "study-harness-probe", 19, 0, result->ai_addr, (int)result->ai_addrlen);
    error = sent == SOCKET_ERROR ? WSAGetLastError() : 0;
    closesocket(sock);
    FreeAddrInfoW(result);
    return error;
}

static int dns_error(const wchar_t *name) {
    struct addrinfoW hints, *result = NULL;
    int status;
    ZeroMemory(&hints, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    status = GetAddrInfoW(name, L"443", &hints, &result);
    if (status == 0) FreeAddrInfoW(result);
    return status;
}

static int pipe_error(const wchar_t *name) {
    HANDLE pipe = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (pipe == INVALID_HANDLE_VALUE) return (int)GetLastError();
    CloseHandle(pipe);
    return 0;
}

/* Three launch styles are tried because an AppContainer may reject some of them for
 * reasons unrelated to policy (image access, current directory, argument form). */
static int spawn_child(const wchar_t *self, const wchar_t *report, int style) {
    STARTUPINFOW startup;
    PROCESS_INFORMATION info;
    wchar_t command[1024];
    DWORD wait;
    BOOL ok;
    const wchar_t *application = style == 0 ? NULL : self;
    ZeroMemory(&startup, sizeof(startup));
    startup.cb = sizeof(startup);
    if (style == 1) {
        _snwprintf_s(command, 1024, _TRUNCATE, L" --child \"%s\"", report);
    } else {
        _snwprintf_s(command, 1024, _TRUNCATE, L"\"%s\" --child \"%s\"", self, report);
    }
    ok = CreateProcessW(application, command, NULL, NULL, FALSE, 0, NULL, style == 2 ? L"." : NULL, &startup, &info);
    if (!ok) return (int)GetLastError();
    wait = WaitForSingleObject(info.hProcess, 10000);
    CloseHandle(info.hThread);
    CloseHandle(info.hProcess);
    return wait == WAIT_OBJECT_0 ? 0 : (int)wait;
}

static int is_app_container(void) {
    HANDLE token;
    DWORD value = 0, bytes = 0;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return -1;
    if (!GetTokenInformation(token, TokenIsAppContainer, &value, sizeof(value), &bytes)) value = (DWORD)-1;
    CloseHandle(token);
    return (int)value;
}

static int read_child_field(const wchar_t *path, const char *field) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    char buffer[256];
    DWORD read = 0, index = 0;
    size_t length;
    int value = -2;
    if (file == INVALID_HANDLE_VALUE) return -3;
    if (!ReadFile(file, buffer, sizeof(buffer) - 1, &read, NULL)) { CloseHandle(file); return -4; }
    CloseHandle(file);
    buffer[read] = '\0';
    length = strlen(field);
    /* JSON object: "field":value -- skip the key's closing quote before the colon. */
    while (index + length + 2 < read) {
        if (_strnicmp(buffer + index, field, length) == 0 &&
            buffer[index + length] == '"' && buffer[index + length + 1] == ':') {
            value = atoi(buffer + index + length + 2);
            break;
        }
        index++;
    }
    return value;
}

int wmain(int argc, wchar_t **argv) {
    WSADATA data;
    DWORD written = 0;
    HANDLE output;
    int startup, container;
    char report[REPORT_MAX];
    int length, inside, outside_read, outside_write, junction_write, runner_pipe, header_read;
    int tcp_v4, tcp_v6, udp, dns, child_spawn, child_container, child_connect;
    int style_a, style_b, style_c;

    if (argc == 3 && wcscmp(argv[1], L"--child") == 0) {
        static const char child_report[] = "{\"isAppContainer\":%d,\"connectError\":%d}";
        char payload[128];
        int socket_error = -1;
        container = is_app_container();
        startup = WSAStartup(MAKEWORD(2, 2), &data);
        if (startup == 0) {
            socket_error = connect_error(AF_INET, 9, L"127.0.0.1");
            WSACleanup();
        } else {
            socket_error = startup;
        }
        length = snprintf(payload, sizeof(payload), child_report, container, socket_error);
        output = CreateFileW(argv[2], GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
        if (output == INVALID_HANDLE_VALUE) return 70;
        WriteFile(output, payload, (DWORD)length, &written, NULL);
        CloseHandle(output);
        return 0;
    }

    if (argc != 13) return 64;
    startup = WSAStartup(MAKEWORD(2, 2), &data);
    container = is_app_container();
    inside = write_error(L"allowed.txt", CREATE_ALWAYS);
    header_read = read_error(argv[11]);
    outside_read = read_error(argv[1]);
    outside_write = write_error(argv[2], CREATE_NEW);
    junction_write = write_error(argv[3], CREATE_NEW);
    runner_pipe = pipe_error(argv[4]);
    if (startup == 0) {
        tcp_v4 = connect_error(AF_INET, (unsigned short)_wtoi(argv[5]), argv[6]);
        tcp_v6 = connect_error(AF_INET6, (unsigned short)_wtoi(argv[5]), L"::1");
        udp = udp_error((unsigned short)_wtoi(argv[7]), argv[6]);
        dns = dns_error(argv[8]);
    } else {
        tcp_v4 = tcp_v6 = udp = dns = startup;
    }
    style_a = spawn_child(argv[9], argv[10], 0);
    style_b = style_a == 0 ? 0 : spawn_child(argv[9], argv[10], 1);
    style_c = (style_a == 0 || style_b == 0) ? 0 : spawn_child(argv[9], argv[10], 2);
    child_spawn = style_a == 0 ? style_a : (style_b == 0 ? style_b : style_c);
    child_container = child_spawn == 0 ? read_child_field(argv[10], "isAppContainer") : -5;
    child_connect = child_spawn == 0 ? read_child_field(argv[10], "connectError") : -5;
    if (startup == 0) WSACleanup();

    length = snprintf(report, sizeof(report),
        "{\"isAppContainer\":%d,\"winsockStartupError\":%d,"
        "\"insideWriteError\":%d,\"headerReadError\":%d,"
        "\"outsideReadError\":%d,\"outsideWriteError\":%d,\"junctionWriteError\":%d,"
        "\"runnerPipeConnectError\":%d,"
        "\"tcpV4Error\":%d,\"tcpV6Error\":%d,\"udpError\":%d,\"dnsError\":%d,"
        "\"childSpawnError\":%d,\"childSpawnStyleB\":%d,\"childSpawnStyleC\":%d,"
        "\"childIsAppContainer\":%d,\"childConnectError\":%d}",
        container, startup, inside, header_read, outside_read, outside_write, junction_write,
        runner_pipe, tcp_v4, tcp_v6, udp, dns, style_a, style_b, style_c, child_container, child_connect);

    output = CreateFileW(argv[12], GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (output == INVALID_HANDLE_VALUE) return 68;
    if (!WriteFile(output, report, (DWORD)length, &written, NULL)) { CloseHandle(output); return 69; }
    CloseHandle(output);
    return 0;
}
