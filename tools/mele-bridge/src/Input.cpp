#include "MeleBridge/Editor.hpp"

#define DIRECTINPUT_VERSION 0x0800
#include <dinput.h>

// While the editor is open the game gets no input: its window messages are queued for ImGui instead, DirectInput8 hands it
// empty mouse and keyboard states, and its cursor calls (re-centring, clipping to a point) are held back.
// The DirectInput8 trap follows SuperAdjustmentStudio's (Reyuu, GPL-3.0, src/render/mouse.cpp): hook the device methods
// through a mouse device of our own, since every device of the process shares one implementation.
namespace MeleBridge::Editor
{
    namespace
    {
        std::atomic<HWND> Window{ nullptr };
        std::atomic<WNDPROC> OriginalWndProc{ nullptr };

        std::mutex QueueLock;
        std::vector<InputEvent> Queue;
        constexpr size_t MaxQueued = 4096;

        using t_SetCursorPos = BOOL WINAPI(int x, int y);
        using t_ClipCursor = BOOL WINAPI(RECT const* rect);
        std::atomic<t_SetCursorPos*> SetCursorPos_orig{ nullptr };
        std::atomic<t_ClipCursor*> ClipCursor_orig{ nullptr };

        /// The clip before the editor opened, and the last one the game asked for while it was open: given back when it closes.
        std::mutex ClipLock;
        RECT ClipBefore{};
        bool HaveClipBefore = false;
        bool GameClipSet = false;
        bool GameClipNull = true;
        RECT GameClip{};

        using t_GetDeviceState = HRESULT STDMETHODCALLTYPE(void* self, DWORD size, void* data);
        using t_GetDeviceData = HRESULT STDMETHODCALLTYPE(void* self, DWORD objectSize, void* objects, DWORD* count, DWORD flags);
        std::atomic<t_GetDeviceState*> GetDeviceStateW_orig{ nullptr };
        std::atomic<t_GetDeviceData*> GetDeviceDataW_orig{ nullptr };
        std::atomic<t_GetDeviceState*> GetDeviceStateA_orig{ nullptr };
        std::atomic<t_GetDeviceData*> GetDeviceDataA_orig{ nullptr };

        /// The loader makes a hook live before it hands back the original: a call that early waits for it.
        template <typename T>
        T* Wait(std::atomic<T*> const& orig)
        {
            auto f = orig.load(std::memory_order_acquire);
            while (f == nullptr)
            {
                YieldProcessor();
                f = orig.load(std::memory_order_acquire);
            }
            return f;
        }

        bool IsInput(UINT message)
        {
            switch (message)
            {
            case WM_KEYDOWN: case WM_KEYUP: case WM_SYSKEYDOWN: case WM_SYSKEYUP: case WM_CHAR:
            case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_LBUTTONDBLCLK:
            case WM_RBUTTONDOWN: case WM_RBUTTONUP: case WM_RBUTTONDBLCLK:
            case WM_MBUTTONDOWN: case WM_MBUTTONUP: case WM_MBUTTONDBLCLK:
            case WM_XBUTTONDOWN: case WM_XBUTTONUP: case WM_XBUTTONDBLCLK:
            case WM_MOUSEWHEEL: case WM_MOUSEHWHEEL:
                return true;
            default:
                return false;
            }
        }

        LRESULT CALLBACK WndProc(HWND window, UINT message, WPARAM w, LPARAM l)
        {
            Diag.WindowThread.store(GetCurrentThreadId(), std::memory_order_relaxed);
            if (Open.load(std::memory_order_acquire))
            {
                if (IsInput(message))
                {
                    {
                        std::scoped_lock lock{ QueueLock };
                        if (Queue.size() < MaxQueued)
                        {
                            Queue.push_back({ message, w, l, (GetKeyState(VK_CONTROL) & 0x8000) != 0, (GetKeyState(VK_SHIFT) & 0x8000) != 0,
                                              (GetKeyState(VK_MENU) & 0x8000) != 0 });
                        }
                    }
                    Diag.SwallowedMessages.fetch_add(1, std::memory_order_relaxed);
                    // Alt+F4 still closes the game (Alt+Tab never reaches a window). Other system keys stay away from
                    // DefWindowProc: F10 or a lone Alt would enter menu mode.
                    if (message == WM_SYSKEYDOWN && w == VK_F4) return DefWindowProcW(window, message, w, l);
                    return 0;
                }
                // The cursor position comes from GetCursorPos; raw input needs DefWindowProc to free its buffer.
                if (message == WM_MOUSEMOVE) return 0;
                if (message == WM_INPUT) return DefWindowProcW(window, message, w, l);
                // ImGui draws the cursor.
                if (message == WM_SETCURSOR && LOWORD(l) == HTCLIENT)
                {
                    SetCursor(nullptr);
                    return TRUE;
                }
            }
            return CallWindowProcW(OriginalWndProc.load(std::memory_order_acquire), window, message, w, l);
        }

        BOOL WINAPI SetCursorPos_hook(int x, int y)
        {
            if (Open.load(std::memory_order_acquire))
            {
                Diag.BlockedCursorCalls.fetch_add(1, std::memory_order_relaxed);
                return TRUE;
            }
            return Wait(SetCursorPos_orig)(x, y);
        }

        BOOL WINAPI ClipCursor_hook(RECT const* rect)
        {
            if (Open.load(std::memory_order_acquire))
            {
                std::scoped_lock lock{ ClipLock };
                GameClipSet = true;
                GameClipNull = rect == nullptr;
                if (rect != nullptr) GameClip = *rect;
                Diag.BlockedCursorCalls.fetch_add(1, std::memory_order_relaxed);
                return TRUE;
            }
            return Wait(ClipCursor_orig)(rect);
        }

        /// Mouse (DIMOUSESTATE, DIMOUSESTATE2) and keyboard states read as "nothing pressed, nothing moved"; other devices (a
        /// joystick's zero is an axis at its end) are left alone.
        HRESULT Zeroed(HRESULT hr, DWORD size, void* data)
        {
            if (Open.load(std::memory_order_acquire) && SUCCEEDED(hr) && data != nullptr
                && (size == sizeof(DIMOUSESTATE) || size == sizeof(DIMOUSESTATE2) || size == 256))
            {
                std::memset(data, 0, size);
                Diag.ZeroedDeviceStates.fetch_add(1, std::memory_order_relaxed);
            }
            return hr;
        }

        /// Buffered events are read (so none pile up for later), then dropped.
        HRESULT Dropped(HRESULT hr, DWORD* count, DWORD flags)
        {
            if (Open.load(std::memory_order_acquire) && SUCCEEDED(hr) && count != nullptr && *count != 0 && !(flags & DIGDD_PEEK))
            {
                *count = 0;
                Diag.ZeroedDeviceData.fetch_add(1, std::memory_order_relaxed);
            }
            return hr;
        }

        HRESULT STDMETHODCALLTYPE GetDeviceStateW_hook(void* self, DWORD size, void* data)
        {
            return Zeroed(Wait(GetDeviceStateW_orig)(self, size, data), size, data);
        }

        HRESULT STDMETHODCALLTYPE GetDeviceDataW_hook(void* self, DWORD objectSize, void* objects, DWORD* count, DWORD flags)
        {
            return Dropped(Wait(GetDeviceDataW_orig)(self, objectSize, objects, count, flags), count, flags);
        }

        HRESULT STDMETHODCALLTYPE GetDeviceStateA_hook(void* self, DWORD size, void* data)
        {
            return Zeroed(Wait(GetDeviceStateA_orig)(self, size, data), size, data);
        }

        HRESULT STDMETHODCALLTYPE GetDeviceDataA_hook(void* self, DWORD objectSize, void* objects, DWORD* count, DWORD flags)
        {
            return Dropped(Wait(GetDeviceDataA_orig)(self, objectSize, objects, count, flags), count, flags);
        }

        /// The device methods' addresses, read from a mouse device of our own, then hooked. Index 9 is GetDeviceState, 10
        /// GetDeviceData (after IUnknown's 3 and GetCapabilities, EnumObjects, GetProperty, SetProperty, Acquire, Unacquire).
        bool HookDevice(::LESDK::Initializer& Init, bool wide, void*& seenState)
        {
            using t_DirectInput8Create = HRESULT WINAPI(HINSTANCE, DWORD, REFIID, void**, LPUNKNOWN);
            auto module = GetModuleHandleW(L"dinput8.dll");
            if (module == nullptr) module = LoadLibraryW(L"dinput8.dll");
            auto const create = module != nullptr ? reinterpret_cast<t_DirectInput8Create*>(GetProcAddress(module, "DirectInput8Create")) : nullptr;
            if (create == nullptr) return false;

            IUnknown* input = nullptr;
            if (FAILED(create(GetModuleHandleW(nullptr), DIRECTINPUT_VERSION, wide ? IID_IDirectInput8W : IID_IDirectInput8A, reinterpret_cast<void**>(&input), nullptr))
                || input == nullptr)
                return false;

            bool hooked = false;
            void* device = nullptr;
            HRESULT const hr = wide ? static_cast<IDirectInput8W*>(static_cast<void*>(input))->CreateDevice(GUID_SysMouse, reinterpret_cast<IDirectInputDevice8W**>(&device), nullptr)
                                    : static_cast<IDirectInput8A*>(static_cast<void*>(input))->CreateDevice(GUID_SysMouse, reinterpret_cast<IDirectInputDevice8A**>(&device), nullptr);
            if (SUCCEEDED(hr) && device != nullptr)
            {
                auto const table = *static_cast<void***>(device);
                // Both interfaces can share one implementation: hook it once.
                if (table[9] == seenState)
                {
                    hooked = true;
                }
                else if (wide)
                {
                    GetDeviceStateW_orig.store(static_cast<t_GetDeviceState*>(Init.InstallHook("MeleBridge IDirectInputDevice8W::GetDeviceState", table[9], GetDeviceStateW_hook)));
                    GetDeviceDataW_orig.store(static_cast<t_GetDeviceData*>(Init.InstallHook("MeleBridge IDirectInputDevice8W::GetDeviceData", table[10], GetDeviceDataW_hook)));
                    hooked = GetDeviceStateW_orig.load() != nullptr && GetDeviceDataW_orig.load() != nullptr;
                    seenState = table[9];
                }
                else
                {
                    GetDeviceStateA_orig.store(static_cast<t_GetDeviceState*>(Init.InstallHook("MeleBridge IDirectInputDevice8A::GetDeviceState", table[9], GetDeviceStateA_hook)));
                    GetDeviceDataA_orig.store(static_cast<t_GetDeviceData*>(Init.InstallHook("MeleBridge IDirectInputDevice8A::GetDeviceData", table[10], GetDeviceDataA_hook)));
                    hooked = GetDeviceStateA_orig.load() != nullptr && GetDeviceDataA_orig.load() != nullptr;
                }
                static_cast<IUnknown*>(device)->Release();
            }
            input->Release();
            return hooked;
        }
    }

    void InstallInput(::LESDK::Initializer& Init)
    {
        void* seenState = nullptr;
        auto const wide = HookDevice(Init, true, seenState);
        auto const ansi = HookDevice(Init, false, seenState);
        Diag.DirectInputHooked.store(wide || ansi);
        LEASI_INFO("editor: DirectInput8 trap {} (W {}, A {})", wide || ansi ? "on" : "FAILED", wide, ansi);

        auto const user32 = GetModuleHandleW(L"user32.dll");
        auto const setCursorPos = user32 != nullptr ? GetProcAddress(user32, "SetCursorPos") : nullptr;
        auto const clipCursor = user32 != nullptr ? GetProcAddress(user32, "ClipCursor") : nullptr;
        if (setCursorPos != nullptr && clipCursor != nullptr)
        {
            SetCursorPos_orig.store(static_cast<t_SetCursorPos*>(Init.InstallHook("MeleBridge SetCursorPos", setCursorPos, SetCursorPos_hook)));
            ClipCursor_orig.store(static_cast<t_ClipCursor*>(Init.InstallHook("MeleBridge ClipCursor", clipCursor, ClipCursor_hook)));
        }
        Diag.CursorHooked.store(SetCursorPos_orig.load() != nullptr && ClipCursor_orig.load() != nullptr);
        LEASI_INFO("editor: cursor hooks {}", Diag.CursorHooked.load() ? "on" : "FAILED");
    }

    void SubclassWindow(HWND window)
    {
        if (Window.load() != nullptr) return;
        // The original must be known before the first message reaches WndProc.
        auto const original = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC));
        OriginalWndProc.store(original, std::memory_order_release);
        auto const replaced = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(WndProc)));
        if (replaced != nullptr && replaced != original) OriginalWndProc.store(replaced, std::memory_order_release);
        Window.store(window);
        Diag.WindowHooked.store(replaced != nullptr);
        LEASI_INFO("editor: window procedure {}", replaced != nullptr ? "subclassed" : "NOT subclassed");
    }

    HWND GameWindow() { return Window.load(std::memory_order_relaxed); }

    std::vector<InputEvent> DrainInput()
    {
        std::vector<InputEvent> events;
        std::scoped_lock lock{ QueueLock };
        events.swap(Queue);
        return events;
    }

    BOOL MoveCursor(int x, int y)
    {
        auto const orig = SetCursorPos_orig.load();
        return orig != nullptr ? orig(x, y) : SetCursorPos(x, y);
    }

    void HoldCursor(bool hold)
    {
        auto const clip = ClipCursor_orig.load();
        if (clip == nullptr) return;
        if (hold)
        {
            {
                std::scoped_lock lock{ ClipLock };
                HaveClipBefore = GetClipCursor(&ClipBefore) != FALSE;
                GameClipSet = false;
            }
            // Inside the window's client area, wherever the game had it.
            RECT client;
            auto const window = GameWindow();
            if (window != nullptr && GetClientRect(window, &client))
            {
                POINT corners[2] = { { client.left, client.top }, { client.right, client.bottom } };
                MapWindowPoints(window, nullptr, corners, 2);
                RECT const screen{ corners[0].x, corners[0].y, corners[1].x, corners[1].y };
                clip(&screen);
            }
            return;
        }
        std::scoped_lock lock{ ClipLock };
        if (GameClipSet) clip(GameClipNull ? nullptr : &GameClip);
        else clip(HaveClipBefore ? &ClipBefore : nullptr);
        GameClipSet = false;
    }
}
