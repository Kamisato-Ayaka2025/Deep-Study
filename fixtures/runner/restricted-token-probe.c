#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <stdlib.h>

static int read_error(const wchar_t *path) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return (int)GetLastError();
    CloseHandle(file);
    return 0;
}

/* Positive socket control entirely inside this process; no external endpoint. */
static int local_socket(void) {
    WSADATA data;
    SOCKET listener = INVALID_SOCKET, client = INVALID_SOCKET, peer = INVALID_SOCKET;
    struct sockaddr_in address = {0};
    int size = sizeof(address), result = WSAStartup(MAKEWORD(2, 2), &data);
    if (result != 0) return result;
    listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listener == INVALID_SOCKET) { result = WSAGetLastError(); goto done; }
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(listener, (struct sockaddr *)&address, size) || listen(listener, 1) ||
        getsockname(listener, (struct sockaddr *)&address, &size)) { result = WSAGetLastError(); goto done; }
    client = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (client == INVALID_SOCKET || connect(client, (struct sockaddr *)&address, size)) {
        result = WSAGetLastError(); goto done;
    }
    peer = accept(listener, NULL, NULL);
    if (peer == INVALID_SOCKET) result = WSAGetLastError();
done:
    if (peer != INVALID_SOCKET) closesocket(peer);
    if (client != INVALID_SOCKET) closesocket(client);
    if (listener != INVALID_SOCKET) closesocket(listener);
    WSACleanup();
    return result;
}

int main(void) {
    int argc;
    wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    HANDLE token;
    DWORD size = 0, app = 0, restricted = 0;
    TOKEN_GROUPS *groups;
    if (!argv || argc != 3) return 64;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return 65;
    GetTokenInformation(token, TokenIsAppContainer, &app, sizeof(app), &size);
    GetTokenInformation(token, TokenRestrictedSids, NULL, 0, &size);
    groups = (TOKEN_GROUPS *)calloc(1, size);
    if (!groups) return 66;
    if (GetTokenInformation(token, TokenRestrictedSids, groups, size, &size)) restricted = groups->GroupCount;
    printf("{\"isAppContainer\":%lu,\"restrictedSidCount\":%lu,\"controlReadError\":%d,\"unrelatedReadError\":%d,\"localSocketError\":%d}\n",
        (unsigned long)app, (unsigned long)restricted, read_error(argv[1]), read_error(argv[2]), local_socket());
    free(groups);
    CloseHandle(token);
    LocalFree(argv);
    return 0;
}
