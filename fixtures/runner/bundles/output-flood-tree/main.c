#include <windows.h>
#include <stdio.h>
#include <string.h>

int main(void)
{
    DWORD written;
    if (strstr(GetCommandLineA(), "--child") != NULL) {
        char buffer[4096];
        memset(buffer, 'x', sizeof(buffer));
        for (;;) {
            if (!WriteFile(GetStdHandle(STD_ERROR_HANDLE), buffer, sizeof(buffer), &written, NULL)) return 2;
        }
    }
    {
        const char *marker = "study-harness-isolated-hello\n";
        char executable[MAX_PATH];
        char command[MAX_PATH + 32];
        STARTUPINFOA startup;
        PROCESS_INFORMATION child;
        if (!WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), marker, (DWORD)strlen(marker), &written, NULL)) return 3;
        if (!GetModuleFileNameA(NULL, executable, MAX_PATH)) return 4;
        if (snprintf(command, sizeof(command), "\"%s\" --child", executable) < 0) return 5;
        ZeroMemory(&startup, sizeof(startup));
        startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESTDHANDLES;
        startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
        startup.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
        startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);
        if (!CreateProcessA(executable, command, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &child)) return 6;
        CloseHandle(child.hThread);
        CloseHandle(child.hProcess);
        Sleep(INFINITE);
    }
    return 0;
}
