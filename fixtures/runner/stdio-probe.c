/* Study Harness P1 stdio fixture (synthetic, fixed, reviewed).
 *
 * Purpose: separate two failure modes that look identical from the host --
 * "the isolated process could not run" versus "the isolated process ran but its
 * standard handles are not usable". The host redirects both streams to files inside
 * the dedicated run directory; this fixture reports what happened on each channel
 * through a file it opens itself, which is the one channel already known to work.
 *
 * It reads nothing outside the run directory and contacts nothing.
 * Exit codes: 0 = the report was written, non-zero = the fixture could not report,
 * which the host records as blocked rather than pass.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

/* Writes through the given handle so the host can tell "no handle" from "handle that
 * refuses writes", and resolves the handle back to a path to prove which file it is. */
static void describe(HANDLE handle, const char *name, char *out, size_t outSize) {
    wchar_t target[512];
    DWORD written = 0, type, length;
    BOOL wrote;
    if (handle == NULL || handle == INVALID_HANDLE_VALUE) {
        _snprintf_s(out, outSize, _TRUNCATE, "%s=absent ", name);
        return;
    }
    type = GetFileType(handle);
    length = GetFinalPathNameByHandleW(handle, target, 512, 0);
    wrote = WriteFile(handle, "x", 1, &written, NULL);
    _snprintf_s(out, outSize, _TRUNCATE, "%s=value:%p type:%lu target:%S wrote:%d/%lu err:%lu ",
        name, handle, (unsigned long)type, length > 0 ? target : L"?", (int)wrote, (unsigned long)written,
        (unsigned long)GetLastError());
}

int wmain(void) {
    char report[2048];
    char inPart[512], outPart[512], errPart[512];
    wchar_t temp[512], cwd[512];
    DWORD tempLength = GetEnvironmentVariableW(L"TEMP", temp, 512);
    DWORD cwdLength = GetCurrentDirectoryW(512, cwd);
    HANDLE file;

    describe(GetStdHandle(STD_INPUT_HANDLE), "stdIn", inPart, sizeof(inPart));
    describe(GetStdHandle(STD_OUTPUT_HANDLE), "stdOut", outPart, sizeof(outPart));
    describe(GetStdHandle(STD_ERROR_HANDLE), "stdErr", errPart, sizeof(errPart));
    _snprintf_s(report, sizeof(report), _TRUNCATE, "%s%s%s tempLength:%lu temp:%S cwdLength:%lu cwd:%S\n",
        inPart, outPart, errPart, (unsigned long)tempLength, tempLength ? temp : L"?",
        (unsigned long)cwdLength, cwdLength ? cwd : L"?");

    file = CreateFileW(L"file-channel.txt", GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return 70;
    {
        DWORD written = 0;
        if (!WriteFile(file, report, (DWORD)strlen(report), &written, NULL)) { CloseHandle(file); return 71; }
    }
    CloseHandle(file);
    return 0;
}
