/* Study Harness P1 source bundle: the escape fixture (synthetic, fixed, reviewed).
 *
 * It is deliberately hostile to the boundary and nothing else. Every target is an argument
 * the trusted Runner chose for this run: a sentinel outside the run directory, a host-created
 * link pointing outside it, a path-traversal form of the same, the real control channel, a
 * decoy with the same ACL shape, sockets the Runner itself owns, a name the Runner has verified
 * resolves on this machine, a file that already carries an ambient ALL APPLICATION PACKAGES
 * read grant, and a header the toolchain legitimately needs. It reports RAW Win32/Winsock
 * numbers as JSON so the caller can re-check the policy instead of trusting a summary.
 *
 * It never compiles, executes or interprets anything supplied by a model or a learner, and it
 * touches synthetic test targets, including this run's own disposable OS profile directory.
 * Exit codes: 0 = matrix completed and report written, 64/68/69/70 = the fixture itself could
 * not run, which the Runner records as a failure rather than a pass.
 *
 * Only C99 and Win32 calls are used: no secure-CRT or MSVC-specific name, because the same
 * source has to build under both MSVC and MinGW with warnings as errors. The wide command
 * line comes from CommandLineToArgvW rather than from wmain, so the same entry point works
 * under both toolchains without a toolchain-specific startup switch.
 *
 * argv: 1 outside file to read, 2 outside file to create, 3 traversal file to create,
 * 4 junction file to create, 5 control pipe, 6 decoy pipe, 7 tcp port, 8 tcp address,
 * 9 udp port, 10 name that resolves on the host, 11 this executable, 12 child report path,
 * 13 toolchain header, 14 report path, 15 ambient AAP-granted file, 16 ipv6 loopback port,
 * 17 this machine's own name, 18 original profile write probe, 19 ARAP-granted file.
 */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <shellapi.h>
#include <userenv.h>
#include <sddl.h>
#include <objbase.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#define REPORT_MAX 2048
#define ARGUMENT_COUNT 20

/* A child receives the synthetic sentinel explicitly, never ambient user data. */

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

static int actual_profile_write(void) {
    HANDLE token; DWORD size=0; TOKEN_APPCONTAINER_INFORMATION *info;
    LPWSTR sid=NULL,folder=NULL; wchar_t path[1024]; int error=-80;
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token)) return -81;
    GetTokenInformation(token,TokenAppContainerSid,NULL,0,&size);
    info=(TOKEN_APPCONTAINER_INFORMATION*)malloc(size);
    if(info && GetTokenInformation(token,TokenAppContainerSid,info,size,&size) &&
       ConvertSidToStringSidW(info->TokenAppContainer,&sid) && SUCCEEDED(GetAppContainerFolderPath(sid,&folder))) {
        swprintf(path,1024,L"%ls\\psec-outside-write.txt",folder);
        error=write_error(path,CREATE_NEW);
        if(error==0) DeleteFileW(path);
    }
    if(folder) CoTaskMemFree(folder);
    if(sid) LocalFree(sid);
    free(info); CloseHandle(token); return error;
}
static int connect_error(int family, unsigned short port, const wchar_t *address) {
    struct addrinfoW hints, *result = NULL;
    SOCKET sock;
    int status, error;
    wchar_t service[8];
    swprintf(service, 8, L"%u", (unsigned)port);
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
    swprintf(service, 8, L"%u", (unsigned)port);
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

/* Local communication inside the container: a listener and a client in the same process. This
 * is the positive half of the network policy -- a mechanism that blocks egress by blocking
 * every socket would fail here rather than pass the negatives by doing nothing. */
static int self_loopback_error(void) {
    SOCKET listener = INVALID_SOCKET, client = INVALID_SOCKET, accepted = INVALID_SOCKET;
    struct sockaddr_in address;
    int length = sizeof(address), error = -2;
    listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listener == INVALID_SOCKET) return (int)WSAGetLastError();
    ZeroMemory(&address, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(0x7F000001);
    address.sin_port = 0;
    if (bind(listener, (struct sockaddr *)&address, sizeof(address)) != 0) {
        error = (int)WSAGetLastError();
    } else if (listen(listener, 1) != 0) {
        error = (int)WSAGetLastError();
    } else if (getsockname(listener, (struct sockaddr *)&address, &length) != 0) {
        error = (int)WSAGetLastError();
    } else {
        client = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (client == INVALID_SOCKET) error = (int)WSAGetLastError();
        else if (connect(client, (struct sockaddr *)&address, sizeof(address)) != 0) error = (int)WSAGetLastError();
        else {
            accepted = accept(listener, NULL, NULL);
            error = accepted == INVALID_SOCKET ? (int)WSAGetLastError() : 0;
        }
    }
    if (accepted != INVALID_SOCKET) closesocket(accepted);
    if (client != INVALID_SOCKET) closesocket(client);
    closesocket(listener);
    return error;
}

/* Whether a path lies inside the work directory. Used for the temporary directory: the
 * mechanism redirects it, and a redirection that lands outside the dedicated directory is a
 * write boundary failure even though the write itself succeeds. */
static int within(const wchar_t *path, const wchar_t *directory) {
    size_t length = wcslen(directory);
    if (_wcsnicmp(path, directory, length) != 0) return 0;
    return path[length] == L'\\' || path[length] == L'\0';
}

/* Three launch styles are tried because a container may reject some of them for reasons
 * unrelated to policy (image access, current directory, argument form). The child is aimed at
 * the same real target the parent probes: a refused connection to a port nobody listens on
 * would look like a denial without being one. That target is filtered for a contained process,
 * so the child's own attempt waits for the connection to time out rather than being refused,
 * and the wait here has to outlast that -- it is the child's observation window, not a verdict
 * on whether the child started. */
static int spawn_child(const wchar_t *self, const wchar_t *report, const wchar_t *address, const wchar_t *port, const wchar_t *sentinel, int style) {
    STARTUPINFOW startup;
    PROCESS_INFORMATION info;
    wchar_t command[1024];
    DWORD wait;
    BOOL ok;
    const wchar_t *application = style == 0 ? NULL : self;
    ZeroMemory(&startup, sizeof(startup));
    startup.cb = sizeof(startup);
    /* %ls, not %s: C99 defines %s in a wide printf as a narrow string, and MinGW follows the
     * standard while MSVC treats %s as wide. With %s the path is read as bytes, so a drive
     * letter followed by a null terminator becomes the whole command line -- "C" -- and the
     * child fails to start with ERROR_FILE_NOT_FOUND under one toolchain only. */
    if (style == 1) {
        swprintf(command, 1024, L" --child \"%ls\" \"%ls\" \"%ls\" \"%ls\"", report, address, port, sentinel);
    } else {
        swprintf(command, 1024, L"\"%ls\" --child \"%ls\" \"%ls\" \"%ls\" \"%ls\"", self, report, address, port, sentinel);
    }
    ok = CreateProcessW(application, command, NULL, NULL, FALSE, 0, NULL, style == 2 ? L"." : NULL, &startup, &info);
    if (!ok) return (int)GetLastError();
    wait = WaitForSingleObject(info.hProcess, 60000);
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

/* Reads one integer field out of the child's JSON report without a JSON parser. */
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
    /* JSON object: "field":value -- the key is followed by its closing quote and a colon. */
    while (index + length + 2 < read) {
        if (strncmp(buffer + index, field, length) == 0 &&
            buffer[index + length] == '"' && buffer[index + length + 1] == ':') {
            value = atoi(buffer + index + length + 2);
            break;
        }
        index++;
    }
    return value;
}

int main(void) {
    WSADATA data;
    DWORD written = 0;
    HANDLE output;
    int startup, container;
    char report[REPORT_MAX];
    int original_profile_write;
    int length, inside, outside_write, traversal_write, junction_write, header_read;
    int control_pipe, decoy_pipe, ambient_aap, ambient_arap, temp_within_work, temp_write, temp_file_within_work;
    int loopback, child_container, child_connect, unrelated_read;
    wchar_t temp_path[MAX_PATH], work_path[MAX_PATH], temp_file[MAX_PATH];
    int tcp_v4, tcp_v6, udp, dns, dns_local_name;
    int style_a, style_b, style_c, child_spawn;
    unsigned short tcp_port, udp_port;
    int argc = 0;
    wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);

    if (argv == NULL) return 64;
    if (argc == 2 && wcscmp(argv[1], L"--resource-probe") == 0) {
        SIZE_T bytes=(SIZE_T)1024*1024*1024+65536;
        void *allocation=VirtualAlloc(NULL,bytes,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
        DWORD memory_error=allocation ? 0 : GetLastError(), spawn_error=0;
        int count;
        if(allocation) VirtualFree(allocation,0,MEM_RELEASE);
        for(count=0;count<20;++count) {
            STARTUPINFOW si; PROCESS_INFORMATION pi; wchar_t command[1024];
            ZeroMemory(&si,sizeof(si)); si.cb=sizeof(si);
            swprintf(command,1024,L"\"%ls\" --hang-leaf",argv[0]);
            if(!CreateProcessW(argv[0],command,NULL,NULL,FALSE,0,NULL,NULL,&si,&pi)) { spawn_error=GetLastError(); break; }
            CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
        }
        printf("{\"memoryError\":%lu,\"spawned\":%d,\"spawnError\":%lu}\n",memory_error,count,spawn_error);
        LocalFree(argv);
        return 0;
    }
    if (argc == 2 && wcscmp(argv[1], L"--hang-leaf") == 0) {
        for (;;) Sleep(1000);
    }
    if (argc == 3 && wcscmp(argv[1], L"--hang-tree") == 0) {
        STARTUPINFOW si;
        PROCESS_INFORMATION pi;
        wchar_t command[1024];
        char ids[128];
        ZeroMemory(&si, sizeof(si)); si.cb = sizeof(si);
        swprintf(command,1024,L"\"%ls\" --hang-leaf",argv[0]);
        if(!CreateProcessW(argv[0],command,NULL,NULL,FALSE,0,NULL,NULL,&si,&pi)) return 71;
        length=snprintf(ids,sizeof(ids),"%lu\n%lu\n",GetCurrentProcessId(),pi.dwProcessId);
        output=CreateFileW(argv[2],GENERIC_WRITE,0,NULL,CREATE_ALWAYS,0,NULL);
        if(output==INVALID_HANDLE_VALUE) return 72;
        if(!WriteFile(output,ids,(DWORD)length,&written,NULL)) return 73;
        CloseHandle(output); CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
        for (;;) Sleep(1000);
    }
    if (argc >= 3 && wcscmp(argv[1], L"--read-aliases") == 0) {
        int index;
        for (index = 2; index < argc; ++index) printf("%d\n", read_error(argv[index]));
        LocalFree(argv);
        return 0;
    }
    if (argc == 6 && wcscmp(argv[1], L"--child") == 0) {
        static const char child_report[] = "{\"isAppContainer\":%d,\"connectError\":%d,\"ambientReadError\":%d}";
        char payload[128];
        int socket_error;
        container = is_app_container();
        startup = WSAStartup(MAKEWORD(2, 2), &data);
        if (startup == 0) {
            socket_error = connect_error(AF_INET, (unsigned short)wcstoul(argv[4], NULL, 10), argv[3]);
            WSACleanup();
        } else {
            socket_error = startup;
        }
        length = snprintf(payload, sizeof(payload), child_report, container, socket_error, read_error(argv[5]));
        output = CreateFileW(argv[2], GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
        if (output == INVALID_HANDLE_VALUE) { LocalFree(argv); return 70; }
        WriteFile(output, payload, (DWORD)length, &written, NULL);
        CloseHandle(output);
        LocalFree(argv);
        return 0;
    }

    if (argc != ARGUMENT_COUNT) { LocalFree(argv); return 64; }
    tcp_port = (unsigned short)wcstoul(argv[7], NULL, 10);
    udp_port = (unsigned short)wcstoul(argv[9], NULL, 10);
    startup = WSAStartup(MAKEWORD(2, 2), &data);
    container = is_app_container();
    inside = write_error(L"allowed.txt", CREATE_ALWAYS);
    /* The header is a genuine toolchain dependency: it must stay readable, and it is the
     * positive control that separates "the container refuses everything" from "the container
     * refuses exactly what it was not given". */
    header_read = read_error(argv[13]);
    /* argv[1] is a synthetic file outside the run directory whose ACL is an ordinary user
     * file's: nothing in it grants the container anything. */
    unrelated_read = read_error(argv[1]);
    /* An ambient grant must not exempt an unrelated file from the required read boundary. */
    ambient_aap = read_error(argv[15]);
    ambient_arap = read_error(argv[19]);
    GetCurrentDirectoryW(MAX_PATH, work_path);
    GetTempPathW(MAX_PATH, temp_path);
    temp_within_work = within(temp_path, work_path);
    /* The path alone is not the boundary: a redirected temporary directory that the container
     * cannot write in would leave a compiler with nowhere to put its intermediate files. */
    temp_file[0] = L'\0';
    temp_write = -2;
    temp_file_within_work = -2;
    if (temp_path[0] != L'\0') {
        wchar_t probe[MAX_PATH];
        swprintf(probe, MAX_PATH, L"%lsstudy-harness-write-probe.txt", temp_path);
        temp_write = write_error(probe, CREATE_ALWAYS);
        DeleteFileW(probe);
        if (GetTempFileNameW(temp_path, L"shp", 0, temp_file) != 0) {
            temp_file_within_work = within(temp_file, work_path);
            DeleteFileW(temp_file);
        } else {
            temp_file_within_work = -1;
        }
    }
    original_profile_write = actual_profile_write();
    if (original_profile_write == 0) DeleteFileW(argv[18]);
    outside_write = write_error(argv[2], CREATE_NEW);
    traversal_write = write_error(argv[3], CREATE_NEW);
    junction_write = write_error(argv[4], CREATE_NEW);
    /* argv[5] is the real control channel and argv[6] a decoy with the same ACL shape. The
     * control channel is the one that matters: reaching it would let the program speak the
     * host protocol, so both are expected to refuse the container and the refusal is measured
     * rather than assumed from a shared parent directory. */
    control_pipe = pipe_error(argv[5]);
    decoy_pipe = pipe_error(argv[6]);
    if (startup == 0) {
        loopback = self_loopback_error();
        tcp_v4 = connect_error(AF_INET, tcp_port, argv[8]);
        tcp_v6 = -9; /* Explicitly out of this host's user-approved test scope. */
        udp = udp_error(udp_port, argv[8]);
        dns = dns_error(argv[10]);
        /* This machine's own name, which the host resolves without leaving the local resolver.
         * Reported as a measurement: it shows what the container's resolver does answer, so the
         * external name's refusal is not read as "DNS is broken in the container". */
        dns_local_name = dns_error(argv[17]);
    } else {
        loopback = tcp_v4 = tcp_v6 = udp = dns = dns_local_name = startup;
    }
    style_a = spawn_child(argv[11], argv[12], argv[8], argv[7], argv[19], 0);
    style_b = style_a == 0 ? 0 : spawn_child(argv[11], argv[12], argv[8], argv[7], argv[19], 1);
    style_c = (style_a == 0 || style_b == 0) ? 0 : spawn_child(argv[11], argv[12], argv[8], argv[7], argv[19], 2);
    child_spawn = style_a == 0 ? style_a : (style_b == 0 ? style_b : style_c);
    child_container = child_spawn == 0 ? read_child_field(argv[12], "isAppContainer") : -5;
    child_connect = child_spawn == 0 ? read_child_field(argv[12], "connectError") : -5;
    if (startup == 0) WSACleanup();

    length = snprintf(report, sizeof(report),
        "{\"originalProfileWriteError\":%d,\"isAppContainer\":%d,\"winsockStartupError\":%d,"
        "\"insideWriteError\":%d,\"headerReadError\":%d,\"containerLoopbackError\":%d,"
        "\"tempWithinWork\":%d,\"tempWriteError\":%d,\"tempFileWithinWork\":%d,"
        "\"unrelatedReadError\":%d,\"ambientAapReadError\":%d,\"ambientArapReadError\":%d,"
        "\"outsideWriteError\":%d,\"traversalWriteError\":%d,\"junctionWriteError\":%d,"
        "\"controlPipeConnectError\":%d,\"decoyPipeConnectError\":%d,"
        "\"tcpV4Error\":%d,\"tcpV6Error\":%d,\"udpError\":%d,\"dnsError\":%d,\"dnsHostNameError\":%d,"
        "\"childSpawnError\":%d,\"childSpawnStyleB\":%d,\"childSpawnStyleC\":%d,"
        "\"childIsAppContainer\":%d,\"childConnectError\":%d}",
        original_profile_write, container, startup, inside, header_read, loopback,
        temp_within_work, temp_write, temp_file_within_work,
        unrelated_read, ambient_aap, ambient_arap, outside_write,
        traversal_write, junction_write, control_pipe, decoy_pipe,
        tcp_v4, tcp_v6, udp, dns, dns_local_name, style_a, style_b, style_c, child_container, child_connect);

    output = CreateFileW(argv[14], GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (output == INVALID_HANDLE_VALUE) { LocalFree(argv); return 68; }
    if (!WriteFile(output, report, (DWORD)length, &written, NULL)) { CloseHandle(output); LocalFree(argv); return 69; }
    CloseHandle(output);
    LocalFree(argv);
    return 0;
}
