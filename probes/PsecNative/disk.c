#include <windows.h>
#include <stdio.h>
#include <string.h>

static char block[1024 * 1024];
int main(int argc, char **argv)
{
    HANDLE file, outside;
    DWORD count, error = 0, read_error;
    unsigned long long total = 0;
    if (argc != 2) return 64;
    outside = CreateFileA(argv[1], GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    read_error = outside == INVALID_HANDLE_VALUE ? GetLastError() : 0;
    if (outside != INVALID_HANDLE_VALUE) CloseHandle(outside);
    memset(block, 'x', sizeof(block));
    file = CreateFileA("capacity-probe.bin", GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_FLAG_WRITE_THROUGH, NULL);
    if (file == INVALID_HANDLE_VALUE) return 65;
    /* Bounded even if the containment mechanism is broken. */
    for (int i = 0; i < 2200; ++i) {
        if (!WriteFile(file, block, sizeof(block), &count, NULL)) { error = GetLastError(); break; }
        total += count;
        if (count != sizeof(block)) { error = ERROR_WRITE_FAULT; break; }
    }
    CloseHandle(file);
    printf("diskError=%lu outsideReadError=%lu bytes=%llu\n", error, read_error, total);
    /* Release only this synthetic file, never recurse over sandbox contents. */
    if (!DeleteFileA("capacity-probe.bin")) return 66;
    return error == ERROR_DISK_FULL && read_error == ERROR_ACCESS_DENIED && total > 0 ? 0 : 1;
}
