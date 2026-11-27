#include <windows.h>
#include <wchar.h>

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR args, int show) {
    wchar_t path[32768], command[32768];
    DWORD n = GetModuleFileNameW(NULL, path, 32768);
    if (!n || n >= 32768) return 1;
    wchar_t *name = wcsrchr(path, L'\\');
    if (!name || wcscpy_s(name + 1, 32768 - (name + 1 - path), L"app\\Mixamp.exe")) return 1;
    if (swprintf(command, 32768, L"\"%ls\" %ls", path, args) < 0) return 1;
    STARTUPINFOW startup = { sizeof(startup) };
    PROCESS_INFORMATION child = {0};
    if (!CreateProcessW(path, command, NULL, NULL, FALSE, 0, NULL, NULL, &startup, &child)) {
        MessageBoxW(NULL, L"Extract the complete ZIP, including the app folder.", L"Mixamp", MB_ICONERROR);
        return 1;
    }
    CloseHandle(child.hThread);
    WaitForSingleObject(child.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(child.hProcess, &code);
    CloseHandle(child.hProcess);
    return (int)code;
}
