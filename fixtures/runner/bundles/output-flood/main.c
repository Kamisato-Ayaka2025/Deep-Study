#include <windows.h>
#include <string.h>

int main(void)
{
    char buffer[4096];
    DWORD written;
    HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    HANDLE error = GetStdHandle(STD_ERROR_HANDLE);
    const char *marker = "study-harness-isolated-hello\n";
    memset(buffer, 'x', sizeof(buffer));
    if (!WriteFile(output, marker, (DWORD)strlen(marker), &written, NULL)) return 2;
    /* Each stream stays below 64 KiB; their combined output must be rejected.
       Exit zero deliberately: a success marker/exit must not override the cap. */
    for (int i = 0; i < 12; ++i) {
        if (!WriteFile(output, buffer, sizeof(buffer), &written, NULL)) return 2;
        if (!WriteFile(error, buffer, sizeof(buffer), &written, NULL)) return 2;
    }
    return 0;
}
