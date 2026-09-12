#include <windows.h>
#include <shlobj.h>
#include <cstdio>
struct Command { const wchar_t* id; const wchar_t* label; bool separator; };
#include "commands.g.h"
int wmain(int argc, wchar_t** argv) {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IShellFolder* folder = nullptr; IContextMenu* context = nullptr;
    HRESULT hr = SHGetDesktopFolder(&folder);
    if (SUCCEEDED(hr)) hr = folder->CreateViewObject(nullptr, IID_PPV_ARGS(&context));
    printf("CreateViewObject=%08lx\n", hr);
    if (context) {
        HMENU menu = CreatePopupMenu();
        hr = context->QueryContextMenu(menu, 0, 1, 0x7fff, CMF_NORMAL);
        printf("QueryContextMenu=%08lx\n", hr);
        int found = 0; UINT selected = 0;
        for (int i = 0; i < GetMenuItemCount(menu); ++i) {
            wchar_t label[256]; char utf8[1024]; GetMenuStringW(menu, i, label, ARRAYSIZE(label), MF_BYPOSITION);
            WideCharToMultiByte(CP_UTF8, 0, label, -1, utf8, ARRAYSIZE(utf8), nullptr, nullptr);
            printf("%s\n", utf8);
            if (lstrcmpW(label, L"Lume 桌面整理") == 0) {
                ++found; HMENU child = GetSubMenu(menu, i);
                for (int row = 0; row < GetMenuItemCount(child); ++row) {
                    GetMenuStringW(child, row, label, ARRAYSIZE(label), MF_BYPOSITION);
                    WideCharToMultiByte(CP_UTF8, 0, label, -1, utf8, ARRAYSIZE(utf8), nullptr, nullptr); printf("  %s\n", utf8);
                    if (argc == 3 && lstrcmpW(argv[1], L"--invoke") == 0)
                        for (const auto& command : commands)
                            if (lstrcmpW(command.id, argv[2]) == 0 && lstrcmpW(command.label, label) == 0) selected = GetMenuItemID(child, row);
                }
            }
        }
        if (found != 1) hr = E_FAIL;
        else if (argc > 1) {
            if (!selected) hr = E_INVALIDARG;
            else { CMINVOKECOMMANDINFO invoke = {sizeof(invoke)}; invoke.lpVerb = MAKEINTRESOURCEA(selected - 1); invoke.nShow = SW_SHOWNORMAL; hr = context->InvokeCommand(&invoke); printf("InvokeCommand=%08lx\n", hr); }
        }
        DestroyMenu(menu); context->Release();
    }
    if (folder) folder->Release(); CoUninitialize();
    return FAILED(hr) ? 1 : 0;
}
