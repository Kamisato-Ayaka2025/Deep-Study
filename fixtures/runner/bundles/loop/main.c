/* Study Harness P1 source bundle: a program that never terminates on its own.
 *
 * It exists to prove the manual-stop half of the hard gate: a program that outlives the
 * observation window keeps running, and stopping it must end its whole tree rather than
 * only the process that was started. It also spawns a descendant, so a stop that misses
 * descendants is observable rather than assumed.
 *
 * The descendant is a copy of this same program rather than a system utility, so "the whole
 * tree is gone" is checkable by process name and does not depend on what else the machine
 * happens to be running. Like the escape fixture it reads its own command line with
 * CommandLineToArgvW, so the same entry point builds under MSVC and MinGW without a
 * toolchain-specific startup switch.
 *
 * The endless wait is written inline in both places, as the last statement of the function,
 * rather than factored into a helper: a loop that never exits makes everything after it
 * unreachable, and unreachable code is a warning-as-error in both toolchains. Sleeping rather
 * than spinning keeps the machine quiet while the run waits to be stopped.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>

static void spawn_descendant(void) {
    STARTUPINFOW startup;
    PROCESS_INFORMATION child;
    wchar_t self[1024];
    wchar_t command[1200];

    ZeroMemory(&startup, sizeof(startup));
    startup.cb = sizeof(startup);
    if (GetModuleFileNameW(NULL, self, 1000) == 0) return;
    /* %ls, not %s: C99 defines %s in a wide printf as a narrow string, and MinGW follows the
     * standard while MSVC treats %s as wide. */
    if (swprintf(command, 1200, L"\"%ls\" --child", self) < 0) return;
    if (CreateProcessW(NULL, command, NULL, NULL, FALSE, 0, NULL, NULL, &startup, &child)) {
        CloseHandle(child.hThread);
        CloseHandle(child.hProcess);
    }
}

int main(void) {
    HANDLE file;
    DWORD written = 0;
    int argc = 0;
    wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);

    if (argv == NULL) return 64;
    if (argc == 2 && lstrcmpW(argv[1], L"--child") == 0) {
        /* The descendant never terminates either: killing only the direct child would
         * leave this one behind, which is exactly what the check looks for. */
        LocalFree(argv);
        for (;;) {
            Sleep(200);
        }
    }
    LocalFree(argv);

    spawn_descendant();
    file = CreateFileW(L"loop-started.txt", GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (file != INVALID_HANDLE_VALUE) {
        WriteFile(file, "running\n", 8, &written, NULL);
        CloseHandle(file);
    }
    for (;;) {
        Sleep(200);
    }
}
