#include <windows.h>
#include <stdio.h>
#include <string.h>

static BOOL spawn(const char *arguments, PROCESS_INFORMATION *child)
{
    char self[MAX_PATH], command[1024];
    STARTUPINFOA startup;
    DWORD length = GetModuleFileNameA(NULL, self, MAX_PATH);
    if (!length || length >= MAX_PATH) return FALSE;
    if (snprintf(command, sizeof(command), "\"%s\" %s", self, arguments) < 0) return FALSE;
    ZeroMemory(&startup, sizeof(startup));
    startup.cb = sizeof(startup);
    return CreateProcessA(self, command, NULL, NULL, FALSE, 0, NULL, NULL, &startup, child);
}
static void reap(PROCESS_INFORMATION *child)
{
    TerminateProcess(child->hProcess, 1);
    WaitForSingleObject(child->hProcess, 5000);
    CloseHandle(child->hThread);
    CloseHandle(child->hProcess);
}
int main(int argc, char **argv)
{
    if (argc == 3 && strcmp(argv[1], "--memory-child") == 0) {
        void *allocation = VirtualAlloc(NULL, (SIZE_T)768 * 1024 * 1024, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        DWORD error = allocation ? 0 : GetLastError(), written;
        HANDLE report = CreateFileA(argv[2], GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_NEW, 0, NULL);
        if (report == INVALID_HANDLE_VALUE) return 2;
        if (!WriteFile(report, &error, sizeof(error), &written, NULL)) { CloseHandle(report); return 3; }
        CloseHandle(report);
        Sleep(INFINITE);
        return 4;
    }
    if (argc == 2 && strcmp(argv[1], "--idle") == 0) { Sleep(INFINITE); return 5; }
    {
        void *allocation = VirtualAlloc(NULL, (SIZE_T)1024 * 1024 * 1024 + 65536, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        DWORD memoryError = allocation ? 0 : GetLastError();
        DWORD aggregate[3] = { 0xffffffff, 0xffffffff, 0xffffffff };
        PROCESS_INFORMATION memoryChildren[3], children[17];
        int memoryCount = 0, count = 0;
        DWORD spawnError = 0;
        JOBOBJECT_CPU_RATE_CONTROL_INFORMATION cpu;
        BOOL cpuKnown;
        if (allocation) VirtualFree(allocation, 0, MEM_RELEASE);
        for (int i = 0; i < 3; ++i) {
            char arguments[128], reportPath[64];
            snprintf(reportPath, sizeof(reportPath), "memory-%d.bin", i);
            snprintf(arguments, sizeof(arguments), "--memory-child %s", reportPath);
            if (!spawn(arguments, &memoryChildren[memoryCount])) break;
            ++memoryCount;
            for (int poll = 0; poll < 60; ++poll) {
                DWORD read = 0;
                HANDLE report = CreateFileA(reportPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
                if (report != INVALID_HANDLE_VALUE) {
                    BOOL ok = ReadFile(report, &aggregate[i], sizeof(DWORD), &read, NULL);
                    CloseHandle(report);
                    if (ok && read == sizeof(DWORD)) break;
                }
                Sleep(50);
            }
        }
        for (int i = 0; i < memoryCount; ++i) reap(&memoryChildren[i]);
        for (int i = 0; i < 17; ++i) {
            if (!spawn("--idle", &children[count])) { spawnError = GetLastError(); break; }
            ++count;
        }
        for (int i = 0; i < count; ++i) reap(&children[i]);
        ZeroMemory(&cpu, sizeof(cpu));
        cpuKnown = QueryInformationJobObject(NULL, JobObjectCpuRateControlInformation, &cpu, sizeof(cpu), NULL);
        printf("memoryError=%lu aggregate0=%lu aggregate1=%lu aggregate2=%lu spawned=%d spawnError=%lu cpuFlags=%lu cpuRate=%lu\n",
            memoryError, aggregate[0], aggregate[1], aggregate[2], count, spawnError, cpu.ControlFlags, cpu.CpuRate);
        if (memoryError != ERROR_COMMITMENT_LIMIT || memoryCount != 3 || aggregate[0] != 0 || aggregate[1] != 0 ||
            aggregate[2] != ERROR_COMMITMENT_LIMIT || count != 15 || spawnError != ERROR_NOT_ENOUGH_QUOTA ||
            !cpuKnown || cpu.ControlFlags != 5 || cpu.CpuRate != 2500) return 1;
        puts("study-harness-isolated-hello");
    }
    return 0;
}
