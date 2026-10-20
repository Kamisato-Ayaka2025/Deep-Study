#include <windows.h>
#include <stdio.h>

/* Fixed synthetic diagnostic: never writes, fills disks, or launches children. */
int main(int argc, char **argv) {
    int i;
    if (argc != 5) return 64;
    for (i = 1; i < argc; ++i) {
        char path[4096], byte = 0;
        DWORD count = 0, error, length;
        HANDLE file = CreateFileA(argv[i], GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        error = file == INVALID_HANDLE_VALUE ? GetLastError() : 0;
        printf("path%d open=%lu", i, error);
        if (file != INVALID_HANDLE_VALUE) {
            BOOL read = ReadFile(file, &byte, 1, &count, NULL);
            error = read ? 0 : GetLastError();
            printf(" read=%lu bytes=%lu", error, count);
            length = GetFinalPathNameByHandleA(file, path, sizeof(path), VOLUME_NAME_DOS);
            error = length ? 0 : GetLastError();
            printf(" finalDos=%lu", error);
            length = GetFinalPathNameByHandleA(file, path, sizeof(path), VOLUME_NAME_NT);
            error = length ? 0 : GetLastError();
            printf(" finalNt=%lu", error);
            CloseHandle(file);
        }
        printf("\n");
    }
    return 0; /* Host assertions determine success, not this diagnostic exit code. */
}
