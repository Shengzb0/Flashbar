// ==WindhawkMod==
// @id              taskbar-ignore-flash
// @name            Taskbar ignore flash
// @description     任务栏忽略 app 的闪烁请求：按钮不变橙、自动隐藏时不弹出（提醒交给 FlashBar）
// @version         1.0
// @author          local
// @include         explorer.exe
// @architecture    x86-64
// ==/WindhawkMod==

// ==WindhawkModReadme==
/*
# Taskbar ignore flash

App 调用 FlashWindow/FlashWindowEx 请求关注时，系统会给任务栏发一条 shell hook
通知（HSHELL_FLASH = 0x8006）。任务栏收到后把按钮变橙；如果开了自动隐藏，
还会把任务栏弹出来并一直停着，直到你切到那个 app。

这个模组在任务栏（taskbar.dll 的 CTaskBand::v_WndProc）里直接丢弃这条通知，
所以任务栏对闪烁完全没有反应。其他程序（例如 FlashBar）照样能收到闪烁通知，
提醒由它们负责。

禁用本模组即可恢复原生行为。
*/
// ==/WindhawkModReadme==

#include <windhawk_utils.h>

using CTaskBand_v_WndProc_t = LRESULT(WINAPI*)(void* pThis,
                                               HWND hWnd,
                                               UINT msg,
                                               WPARAM wParam,
                                               LPARAM lParam);
CTaskBand_v_WndProc_t CTaskBand_v_WndProc_Original;

UINT g_shellHookMsg;

LRESULT WINAPI CTaskBand_v_WndProc_Hook(void* pThis,
                                        HWND hWnd,
                                        UINT msg,
                                        WPARAM wParam,
                                        LPARAM lParam) {
    // HSHELL_FLASH = HSHELL_REDRAW | 0x8000：丢弃，任务栏不变橙也不弹出
    if (msg == g_shellHookMsg && g_shellHookMsg != 0 &&
        (wParam & 0xFFFF) == HSHELL_FLASH) {
        return 0;
    }
    return CTaskBand_v_WndProc_Original(pThis, hWnd, msg, wParam, lParam);
}

BOOL Wh_ModInit() {
    Wh_Log(L">");

    // 同一会话里 RegisterWindowMessage 返回的 ID 相同，任务栏收到的也是这个 ID
    g_shellHookMsg = RegisterWindowMessage(L"SHELLHOOK");
    Wh_Log(L"SHELLHOOK msg = 0x%X", g_shellHookMsg);

    HMODULE taskbarModule = LoadLibrary(L"taskbar.dll");
    if (!taskbarModule) {
        Wh_Log(L"taskbar.dll not loaded");
        return FALSE;
    }

    WindhawkUtils::SYMBOL_HOOK hooks[] = {
        {
            {LR"(protected: virtual __int64 __cdecl CTaskBand::v_WndProc(struct HWND__ *,unsigned int,unsigned __int64,__int64))"},
            &CTaskBand_v_WndProc_Original,
            CTaskBand_v_WndProc_Hook,
        },
    };

    if (!WindhawkUtils::HookSymbols(taskbarModule, hooks, ARRAYSIZE(hooks))) {
        Wh_Log(L"HookSymbols failed");
        return FALSE;
    }

    return TRUE;
}
