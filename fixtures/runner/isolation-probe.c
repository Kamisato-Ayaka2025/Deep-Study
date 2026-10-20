#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

/* Fixed, reviewed feasibility fixture. Never accepts source code or shell commands. */
static int read_access(const wchar_t *path) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return (int)GetLastError();
    CloseHandle(file);
    return 0;
}
static int write_access(const wchar_t *path) {
    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_NEW, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return (int)GetLastError();
    CloseHandle(file);
    return 0;
}
static int loopback(unsigned short port) {
    SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in address;
    int result;
    if (sock == INVALID_SOCKET) return WSAGetLastError();
    ZeroMemory(&address, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(port);
    result = connect(sock, (struct sockaddr *)&address, sizeof(address));
    result = result == 0 ? 0 : WSAGetLastError();
    closesocket(sock);
    return result;
}
int wmain(int argc, wchar_t **argv) {
    HANDLE token;
    DWORD is_container = 0, bytes = 0, written = 0;
    WSADATA data;
    char report[512];
    HANDLE output;
    int inside, outside_read, outside_write, local, length, startup;
    if (argc != 4) return 64;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return 65;
    if (!GetTokenInformation(token, TokenIsAppContainer, &is_container, sizeof(is_container), &bytes)) return 66;
    CloseHandle(token);
    startup = WSAStartup(MAKEWORD(2, 2), &data);
    inside = write_access(L"allowed.txt");
    outside_read = read_access(argv[1]);
    outside_write = write_access(argv[2]);
    local = startup == 0 ? loopback((unsigned short)_wtoi(argv[3])) : startup;
    if (startup == 0) WSACleanup();
    length = snprintf(report, sizeof(report), "{\"isAppContainer\":%lu,\"insideWriteError\":%d,\"outsideReadError\":%d,\"outsideWriteError\":%d,\"winsockStartupError\":%d,\"loopbackError\":%d}",
        (unsigned long)is_container, inside, outside_read, outside_write, startup, local);
    output = CreateFileW(L"probe.json", GENERIC_WRITE, 0, NULL, CREATE_NEW, 0, NULL);
    if (output == INVALID_HANDLE_VALUE) return 68;
    if (!WriteFile(output, report, (DWORD)length, &written, NULL)) return 69;
    CloseHandle(output);
    return 0;
}
