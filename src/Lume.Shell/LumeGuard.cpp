#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <cstdint>
#include <cstdlib>
#include <string>
#include <vector>

// Fixed little-endian lease written by DesktopRecovery.SaveNativeLease.
// No JSON runtime, CLR, polling loop or UI framework is loaded by this process.
struct Entry { uint64_t handle; uint32_t pid; uint32_t visible; char className[64]; };
static_assert(sizeof(Entry) == 80);
static constexpr uint32_t Magic = 0x31474d4c;
static constexpr uint64_t FileTimeEpoch = 504911232000000000ULL;

static bool Restore(const std::wstring& path) {
    const auto native = path + L".native";
    HANDLE file = CreateFileW(native.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError() == ERROR_FILE_NOT_FOUND;
    LARGE_INTEGER size{}; DWORD bytes = 0; uint32_t header[2]{};
    bool valid = GetFileSizeEx(file, &size) && ReadFile(file, header, sizeof(header), &bytes, nullptr) && bytes == sizeof(header)
        && header[0] == Magic && header[1] >= 1 && header[1] <= 65
        && size.QuadPart == static_cast<LONGLONG>(sizeof(header) + header[1] * sizeof(Entry));
    std::vector<Entry> entries;
    if (valid) {
        entries.resize(header[1]);
        const DWORD expected = static_cast<DWORD>(entries.size() * sizeof(Entry));
        valid = ReadFile(file, entries.data(), expected, &bytes, nullptr) && bytes == expected;
    }
    CloseHandle(file);
    // Validate the entire payload before touching any window.
    for (const auto& entry : entries) {
        if (entry.className[63] != 0 || entry.visible > 1 || entry.pid == 0 || entry.handle == 0
            || (strcmp(entry.className, "SysListView32") != 0 && strcmp(entry.className, "TXMiniSkin") != 0)) valid = false;
    }
    if (!valid) return false;
    bool restored = true;
    for (const auto& entry : entries) {
        auto window = reinterpret_cast<HWND>(static_cast<uintptr_t>(entry.handle));
        DWORD pid = 0; char name[64]{};
        GetWindowThreadProcessId(window, &pid); GetClassNameA(window, name, 64);
        if (pid == entry.pid && strcmp(name, entry.className) == 0)
            restored = (ShowWindowAsync(window, entry.visible ? SW_SHOW : SW_HIDE) != FALSE) && restored;
    }
    if (restored) { DeleteFileW(native.c_str()); DeleteFileW(path.c_str()); }
    return restored;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int argc = 0; auto argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 2;
    if (argc != 4) { LocalFree(argv); return 2; }
    wchar_t* endPid = nullptr; wchar_t* endTicks = nullptr;
    const auto rawPid = wcstoull(argv[1], &endPid, 10); const auto ticks = wcstoull(argv[2], &endTicks, 10);
    const bool valid = rawPid > 0 && rawPid <= MAXDWORD && endPid != argv[1] && *endPid == 0
        && endTicks != argv[2] && *endTicks == 0 && ticks >= FileTimeEpoch;
    const std::wstring path(argv[3]); LocalFree(argv);
    if (!valid || path.empty()) return 2;
    HANDLE parent = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, static_cast<DWORD>(rawPid));
    if (!parent) return GetLastError() == ERROR_INVALID_PARAMETER && Restore(path) ? 0 : 3;
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(parent, &created, &exited, &kernel, &user)
        || ((static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime) != ticks - FileTimeEpoch) {
        CloseHandle(parent); return 4;
    }
    const auto ready = path + L".ready";
    HANDLE signal = CreateFileW(ready.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (signal == INVALID_HANDLE_VALUE) { CloseHandle(parent); return 5; }
    CloseHandle(signal);
    const auto wait = WaitForSingleObject(parent, INFINITE); CloseHandle(parent);
    const bool restored = wait == WAIT_OBJECT_0 && Restore(path);
    DeleteFileW(ready.c_str());
    return restored ? 0 : 6;
}
