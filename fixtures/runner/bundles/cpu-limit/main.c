#include <windows.h>
#include <stdio.h>

static volatile LONG stop = 0;
static DWORD WINAPI spin(LPVOID ignored)
{
    (void)ignored;
    while (InterlockedCompareExchange(&stop, 0, 0) == 0) { }
    return 0;
}
static unsigned long long ticks(FILETIME time)
{
    return ((unsigned long long)time.dwHighDateTime << 32) | time.dwLowDateTime;
}
int main(void)
{
    SYSTEM_INFO system;
    HANDLE threads[MAXIMUM_WAIT_OBJECTS];
    FILETIME created, exited, kernelBefore, userBefore, kernelAfter, userAfter;
    ULONGLONG start, elapsed;
    unsigned long long cpu;
    DWORD usage, count = 0;
    GetSystemInfo(&system);
    if (system.dwNumberOfProcessors < 1 || system.dwNumberOfProcessors > MAXIMUM_WAIT_OBJECTS) return 2;
    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernelBefore, &userBefore)) return 3;
    start = GetTickCount64();
    for (DWORD i = 0; i < system.dwNumberOfProcessors; ++i) {
        threads[count] = CreateThread(NULL, 0, spin, NULL, 0, NULL);
        if (!threads[count]) break;
        ++count;
    }
    Sleep(8000);
    InterlockedExchange(&stop, 1);
    if (count) WaitForMultipleObjects(count, threads, TRUE, INFINITE);
    elapsed = GetTickCount64() - start;
    for (DWORD i = 0; i < count; ++i) CloseHandle(threads[i]);
    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernelAfter, &userAfter)) return 4;
    cpu = ticks(kernelAfter) + ticks(userAfter) - ticks(kernelBefore) - ticks(userBefore);
    if (!elapsed) return 5;
    usage = (DWORD)(cpu / (10 * elapsed * system.dwNumberOfProcessors));
    printf("cpuUsagePermille=%lu elapsedMs=%llu workers=%lu processors=%lu\n", usage, elapsed, count, system.dwNumberOfProcessors);
    /* A lower control bound detects a workload that never exercised the cap.
       Allow scheduling/accounting tolerance around the configured 25%. */
    if (count != system.dwNumberOfProcessors || usage < 100 || usage > 350) return 1;
    puts("study-harness-isolated-hello");
    return 0;
}
