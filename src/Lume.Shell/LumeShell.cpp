#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <strsafe.h>
#include <new>

// 原生、无托管运行时的 Explorer 扩展；只构造菜单并启动相邻的 Lume.exe。
static const CLSID CLSID_Lume = {0xa972e74d,0xd168,0x4a54,{0x96,0x3a,0x65,0x14,0xbd,0x37,0xcb,0x74}};
static HMODULE module;
static LONG objects;
struct Command { const wchar_t* id; const wchar_t* label; bool separator; };
#include "commands.g.h"
static constexpr UINT count = ARRAYSIZE(commands);

class Menu final : public IShellExtInit, public IContextMenu {
    LONG refs = 1;
    bool desktop = false;
    HBITMAP icon = nullptr;
public:
    Menu() { InterlockedIncrement(&objects); }
    ~Menu() { if (icon) DeleteObject(icon); InterlockedDecrement(&objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (iid == IID_IUnknown || iid == IID_IShellExtInit) *out = static_cast<IShellExtInit*>(this);
        else if (iid == IID_IContextMenu) *out = static_cast<IContextMenu*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE Initialize(PCIDLIST_ABSOLUTE folder, IDataObject* selection, HKEY) override {
        desktop = false;
        // Directory\Background 注册也会收到普通文件夹，必须逐次拒绝。
        if (!folder || selection) return E_INVALIDARG;
        if (ILIsEmpty(folder)) desktop = true;
        else {
            PIDLIST_ABSOLUTE known = nullptr;
            if (SUCCEEDED(SHGetKnownFolderIDList(FOLDERID_Desktop, 0, nullptr, &known))) {
                desktop = ILIsEqual(folder, known) != FALSE; CoTaskMemFree(known);
            }
        }
        return desktop ? S_OK : E_INVALIDARG;
    }
    HRESULT STDMETHODCALLTYPE QueryContextMenu(HMENU parent, UINT position, UINT first, UINT last, UINT flags) override {
        if (!desktop || (flags & CMF_DEFAULTONLY) || last < first || last - first + 1 < count) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        HMENU child = CreatePopupMenu(); if (!child) return HRESULT_FROM_WIN32(GetLastError());
        for (UINT i = 0; i < count; ++i) {
            if (commands[i].separator && !AppendMenuW(child, MF_SEPARATOR, 0, nullptr)) { DestroyMenu(child); return E_FAIL; }
            if (!AppendMenuW(child, MF_STRING, first + i, commands[i].label)) { DestroyMenu(child); return E_FAIL; }
        }
        MENUITEMINFOW item = {sizeof(item)};
        item.fMask = MIIM_STRING | MIIM_SUBMENU; item.dwTypeData = const_cast<wchar_t*>(L"Lume 桌面整理"); item.hSubMenu = child;
        if (!icon) {
            BITMAPINFO info = {}; info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = 16; info.bmiHeader.biHeight = -16; info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32;
            void* pixels = nullptr; icon = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
            if (icon && pixels) {
                auto data = static_cast<DWORD*>(pixels);
                for (int y = 0; y < 16; ++y) for (int x = 0; x < 16; ++x)
                    data[y * 16 + x] = (x >= 5 && x <= 7 && y >= 3 && y <= 12) || (x >= 5 && x <= 12 && y >= 10 && y <= 12) ? 0xffffffff : 0xff438b72;
            }
        }
        if (icon) { item.fMask |= MIIM_BITMAP; item.hbmpItem = icon; }
        if (!InsertMenuItemW(parent, position, TRUE, &item)) { DestroyMenu(child); return HRESULT_FROM_WIN32(GetLastError()); }
        return MAKE_HRESULT(SEVERITY_SUCCESS, 0, count);
    }
    HRESULT STDMETHODCALLTYPE InvokeCommand(CMINVOKECOMMANDINFO* info) override {
        if (!desktop || !info || info->cbSize < sizeof(CMINVOKECOMMANDINFO)) return E_INVALIDARG;
        UINT index = count;
        if (IS_INTRESOURCE(info->lpVerb)) index = LOWORD(info->lpVerb);
        else {
            const auto ex = reinterpret_cast<CMINVOKECOMMANDINFOEX*>(info);
            for (UINT i = 0; i < count; ++i) {
                wchar_t verb[80]; StringCchPrintfW(verb, ARRAYSIZE(verb), L"lume.%s", commands[i].id);
                if (info->cbSize >= sizeof(*ex) && (info->fMask & CMIC_MASK_UNICODE)) {
                    if (ex->lpVerbW && !IS_INTRESOURCE(ex->lpVerbW) && lstrcmpW(ex->lpVerbW, verb) == 0) index = i;
                } else {
                    char narrow[80]; WideCharToMultiByte(CP_UTF8, 0, verb, -1, narrow, ARRAYSIZE(narrow), nullptr, nullptr);
                    if (lstrcmpA(info->lpVerb, narrow) == 0) index = i;
                }
            }
        }
        if (index >= count) return E_INVALIDARG;
        wchar_t exe[32768];
        DWORD length = GetModuleFileNameW(module, exe, ARRAYSIZE(exe));
        if (!length || length >= ARRAYSIZE(exe)) return E_FAIL;
        wchar_t* slash = wcsrchr(exe, L'\\'); if (!slash) return E_FAIL;
        if (FAILED(StringCchCopyW(slash + 1, ARRAYSIZE(exe) - (slash + 1 - exe), L"Lume.exe"))) return E_FAIL;
        wchar_t args[100]; StringCchPrintfW(args, ARRAYSIZE(args), L"--action %s", commands[index].id);
        SHELLEXECUTEINFOW launch = {sizeof(launch)}; launch.fMask = SEE_MASK_FLAG_NO_UI;
        launch.hwnd = info->hwnd; launch.lpFile = exe; launch.lpParameters = args; launch.nShow = SW_SHOWNORMAL;
        return ShellExecuteExW(&launch) ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }
    HRESULT STDMETHODCALLTYPE GetCommandString(UINT_PTR id, UINT type, UINT*, LPSTR output, UINT size) override {
        if (id >= count) return E_INVALIDARG;
        if (type == GCS_VALIDATEA || type == GCS_VALIDATEW) return S_OK;
        if (!output || !size) return E_INVALIDARG;
        wchar_t text[100];
        if (type == GCS_VERBA || type == GCS_VERBW) StringCchPrintfW(text, ARRAYSIZE(text), L"lume.%s", commands[id].id);
        else if (type == GCS_HELPTEXTA || type == GCS_HELPTEXTW) StringCchCopyW(text, ARRAYSIZE(text), commands[id].label);
        else return E_INVALIDARG;
        if (type & GCS_UNICODE) return StringCchCopyW(reinterpret_cast<LPWSTR>(output), size, text);
        return WideCharToMultiByte(CP_ACP, 0, text, -1, output, size, nullptr, nullptr) ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }
};
class Factory final : public IClassFactory {
    LONG refs = 1;
public:
    Factory() { InterlockedIncrement(&objects); }
    ~Factory() { InterlockedDecrement(&objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (iid != IID_IUnknown && iid != IID_IClassFactory) return E_NOINTERFACE;
        *out = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** out) override {
        if (!out) return E_POINTER; *out = nullptr; if (outer) return CLASS_E_NOAGGREGATION;
        auto object = new(std::nothrow) Menu(); if (!object) return E_OUTOFMEMORY;
        auto result = object->QueryInterface(iid, out); object->Release(); return result;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&objects); else InterlockedDecrement(&objects); return S_OK; }
};
HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** out) {
    if (!out) return E_POINTER; *out = nullptr;
    if (clsid != CLSID_Lume) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new(std::nothrow) Factory(); if (!factory) return E_OUTOFMEMORY;
    auto result = factory->QueryInterface(iid, out); factory->Release(); return result;
}
HRESULT __stdcall DllCanUnloadNow() { return objects == 0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) { module = instance; DisableThreadLibraryCalls(instance); } return TRUE; }

// 隔离进程中验证真实 COM 对象和 Win32 菜单，不启动程序、不操作用户文件。
extern "C" __declspec(dllexport) int __stdcall LumeVerify() {
    Menu* menu = new(std::nothrow) Menu(); if (!menu) return 1;
    HMENU parent = CreatePopupMenu(); int result = 0;
    ITEMIDLIST desktop = {};
    if (menu->Initialize(&desktop, nullptr, nullptr) != S_OK) result = 2;
    if (!result && menu->QueryContextMenu(parent, 0, 100, 100 + count - 1, CMF_NORMAL) != static_cast<HRESULT>(count)) result = 3;
    HMENU child = GetSubMenu(parent, 0); UINT row = 0;
    for (UINT i = 0; !result && i < count; ++i) {
        if (commands[i].separator) ++row;
        wchar_t label[100], verb[100], expected[100];
        GetMenuStringW(child, row, label, ARRAYSIZE(label), MF_BYPOSITION);
        StringCchPrintfW(expected, ARRAYSIZE(expected), L"lume.%s", commands[i].id);
        if (GetMenuItemID(child, row++) != 100 + i || lstrcmpW(label, commands[i].label)) result = 4;
        if (menu->GetCommandString(i, GCS_VERBW, nullptr, reinterpret_cast<LPSTR>(verb), ARRAYSIZE(verb)) != S_OK || lstrcmpW(verb, expected)) result = 5;
    }
    if (!result && menu->QueryContextMenu(parent, 0, 0, 1, CMF_NORMAL) != S_OK) result = 6;
    if (!result && menu->QueryContextMenu(parent, 0, 0, 100, CMF_DEFAULTONLY) != S_OK) result = 7;
    CMINVOKECOMMANDINFO bad = {sizeof(bad)}; bad.lpVerb = "powershell";
    if (!result && menu->InvokeCommand(&bad) != E_INVALIDARG) result = 8;
    PIDLIST_ABSOLUTE folder = nullptr;
    if (SUCCEEDED(SHGetKnownFolderIDList(FOLDERID_Windows, 0, nullptr, &folder))) {
        if (!result && menu->Initialize(folder, nullptr, nullptr) != E_INVALIDARG) result = 9;
        CoTaskMemFree(folder);
        if (!result && menu->QueryContextMenu(parent, 0, 0, 100, 0) != S_OK) result = 10;
    } else result = 11;
    DestroyMenu(parent); menu->Release();
    return result ? result : (DllCanUnloadNow() == S_OK ? 0 : 12);
}
