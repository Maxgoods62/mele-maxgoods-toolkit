#include "MeleBridge/Editor.hpp"

#include <d3d11.h>
#include <dxgi1_2.h>

#include <cmath>
#include <algorithm>
#include <cstring>

#pragma warning(push, 0)
#include "imgui.h"
#include "backends/imgui_impl_dx11.h"
#include "ImGuizmo.h"
#pragma warning(pop)

// The editor's render-thread half: Dear ImGui and ImGuizmo drawn into the game's back buffer from a hooked
// IDXGISwapChain::Present. It never touches game objects: it draws from the game thread's snapshot and posts commands back.
namespace MeleBridge::Editor
{
    namespace
    {
        using t_Present = HRESULT STDMETHODCALLTYPE(IDXGISwapChain* chain, UINT sync, UINT flags);
        using t_Present1 = HRESULT STDMETHODCALLTYPE(IDXGISwapChain1* chain, UINT sync, UINT flags, DXGI_PRESENT_PARAMETERS const* parameters);
        using t_ResizeBuffers = HRESULT STDMETHODCALLTYPE(IDXGISwapChain* chain, UINT count, UINT width, UINT height, DXGI_FORMAT format, UINT flags);
        std::atomic<t_Present*> Present_orig{ nullptr };
        std::atomic<t_Present1*> Present1_orig{ nullptr };
        std::atomic<t_ResizeBuffers*> ResizeBuffers_orig{ nullptr };

        constexpr float NearPlane = 10;
        constexpr float FarPlane = 1e6f;
        constexpr float UnitsToRadians = 3.14159265f / 32768.f;

        // Render thread only.
        IDXGISwapChain* GameChain = nullptr;
        ID3D11Device* Device = nullptr;
        ID3D11DeviceContext* Context = nullptr;
        ID3D11RenderTargetView* Target = nullptr;
        bool Ready = false;
        bool Failed = false;
        bool WasOpen = false;
        thread_local bool InPresent = false;
        LARGE_INTEGER LastFrame{};
        LARGE_INTEGER LastCalibrationLog{};
        bool ShowDiagnostics = true;
        bool ShowDemo = false;
        bool Dragging = false;
        std::string DraggedPath;
        float DragModel[16]{};
        bool HadFocus = true;
        /// Where the cursor was when a free-cam look started: it goes back there when the button is released.
        POINT LookStart{};

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

        // ! Camera matrices
        // ========================================

        /// UE3's view: world → camera space with x right, y up, z forward (depth), as rows for row vectors (v * M), which is
        /// both FMatrix's and ImGuizmo's layout. The axes are FRotationMatrix's rows.
        void ViewMatrix(FTPOV const& pov, float* m)
        {
            auto const p = static_cast<float>(pov.Rotation.Pitch) * UnitsToRadians;
            auto const y = static_cast<float>(pov.Rotation.Yaw) * UnitsToRadians;
            auto const r = static_cast<float>(pov.Rotation.Roll) * UnitsToRadians;
            auto const sp = std::sin(p), cp = std::cos(p), sy = std::sin(y), cy = std::cos(y), sr = std::sin(r), cr = std::cos(r);
            float const forward[3] = { cp * cy, cp * sy, sp };
            float const right[3] = { sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp };
            float const up[3] = { -(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp };
            float const loc[3] = { pov.Location.X, pov.Location.Y, pov.Location.Z };
            auto const dot = [&](float const* a) { return a[0] * loc[0] + a[1] * loc[1] + a[2] * loc[2]; };
            float const out[16] = {
                right[0], up[0], forward[0], 0,
                right[1], up[1], forward[1], 0,
                right[2], up[2], forward[2], 0,
                -dot(right), -dot(up), -dot(forward), 1,
            };
            std::memcpy(m, out, sizeof out);
        }

        /// UE3's perspective: left-handed, FOV horizontal, near plane 10 (LocalPlayer.NearClipPlane).
        void ProjectionMatrix(float fovDegrees, float width, float height, float* m)
        {
            auto const x = 1.f / std::tan(fovDegrees * 3.14159265f / 360.f);
            auto const y = x * width / height;
            auto const q = FarPlane / (FarPlane - NearPlane);
            float const out[16] = {
                x, 0, 0, 0,
                0, y, 0, 0,
                0, 0, q, 1,
                0, 0, -q * NearPlane, 0,
            };
            std::memcpy(m, out, sizeof out);
        }

        void Transform(float const* v, float const* m, float* out)
        {
            for (int j = 0; j < 4; j++) out[j] = v[0] * m[j] + v[1] * m[4 + j] + v[2] * m[8 + j] + v[3] * m[12 + j];
        }

        /// A world point in back-buffer pixels, through the same matrices the gizmo uses. False behind the camera.
        bool ProjectPoint(float const* view, float const* projection, FVector point, float width, float height, float& x, float& y)
        {
            float const world[4] = { point.X, point.Y, point.Z, 1 };
            float eye[4], clip[4];
            Transform(world, view, eye);
            Transform(eye, projection, clip);
            if (clip[3] <= 0.001f) return false;
            x = (clip[0] / clip[3] * 0.5f + 0.5f) * width;
            y = (0.5f - clip[1] / clip[3] * 0.5f) * height;
            return true;
        }

        /// ImGuizmo only ends a drag inside Manipulate: when Manipulate is skipped (no selection, editor closed), its drag stays
        /// on and IsOver/IsUsing would keep every click from reaching the world.
        void ResetGizmo()
        {
            Dragging = false;
            if (ImGuizmo::IsUsingAny())
            {
                ImGuizmo::Enable(false);
                ImGuizmo::Enable(true);
            }
        }

        // ! Input
        // ========================================

        ImGuiKey KeyOf(WPARAM vk)
        {
            if (vk >= '0' && vk <= '9') return static_cast<ImGuiKey>(ImGuiKey_0 + static_cast<int>(vk - '0'));
            if (vk >= 'A' && vk <= 'Z') return static_cast<ImGuiKey>(ImGuiKey_A + static_cast<int>(vk - 'A'));
            if (vk >= VK_F1 && vk <= VK_F12) return static_cast<ImGuiKey>(ImGuiKey_F1 + static_cast<int>(vk - VK_F1));
            switch (vk)
            {
            case VK_TAB: return ImGuiKey_Tab;
            case VK_LEFT: return ImGuiKey_LeftArrow;
            case VK_RIGHT: return ImGuiKey_RightArrow;
            case VK_UP: return ImGuiKey_UpArrow;
            case VK_DOWN: return ImGuiKey_DownArrow;
            case VK_PRIOR: return ImGuiKey_PageUp;
            case VK_NEXT: return ImGuiKey_PageDown;
            case VK_HOME: return ImGuiKey_Home;
            case VK_END: return ImGuiKey_End;
            case VK_INSERT: return ImGuiKey_Insert;
            case VK_DELETE: return ImGuiKey_Delete;
            case VK_BACK: return ImGuiKey_Backspace;
            case VK_SPACE: return ImGuiKey_Space;
            case VK_RETURN: return ImGuiKey_Enter;
            case VK_ESCAPE: return ImGuiKey_Escape;
            case VK_CONTROL: return ImGuiKey_LeftCtrl;
            case VK_SHIFT: return ImGuiKey_LeftShift;
            case VK_MENU: return ImGuiKey_LeftAlt;
            case VK_OEM_MINUS: return ImGuiKey_Minus;
            case VK_OEM_PLUS: return ImGuiKey_Equal;
            case VK_OEM_PERIOD: return ImGuiKey_Period;
            case VK_OEM_COMMA: return ImGuiKey_Comma;
            default: return ImGuiKey_None;
            }
        }

        void FeedInput(ImGuiIO& io, float scaleX, float scaleY)
        {
            for (auto const& e : DrainInput())
            {
                io.AddKeyEvent(ImGuiMod_Ctrl, e.Ctrl);
                io.AddKeyEvent(ImGuiMod_Shift, e.Shift);
                io.AddKeyEvent(ImGuiMod_Alt, e.Alt);
                switch (e.Message)
                {
                case WM_LBUTTONDOWN: case WM_LBUTTONDBLCLK: io.AddMouseButtonEvent(ImGuiMouseButton_Left, true); break;
                case WM_LBUTTONUP: io.AddMouseButtonEvent(ImGuiMouseButton_Left, false); break;
                case WM_RBUTTONDOWN: case WM_RBUTTONDBLCLK: io.AddMouseButtonEvent(ImGuiMouseButton_Right, true); break;
                case WM_RBUTTONUP: io.AddMouseButtonEvent(ImGuiMouseButton_Right, false); break;
                case WM_MBUTTONDOWN: case WM_MBUTTONDBLCLK: io.AddMouseButtonEvent(ImGuiMouseButton_Middle, true); break;
                case WM_MBUTTONUP: io.AddMouseButtonEvent(ImGuiMouseButton_Middle, false); break;
                case WM_XBUTTONDOWN: case WM_XBUTTONDBLCLK: io.AddMouseButtonEvent(GET_XBUTTON_WPARAM(e.WParam) == XBUTTON1 ? 3 : 4, true); break;
                case WM_XBUTTONUP: io.AddMouseButtonEvent(GET_XBUTTON_WPARAM(e.WParam) == XBUTTON1 ? 3 : 4, false); break;
                case WM_MOUSEWHEEL: io.AddMouseWheelEvent(0, static_cast<float>(GET_WHEEL_DELTA_WPARAM(e.WParam)) / WHEEL_DELTA); break;
                case WM_MOUSEHWHEEL: io.AddMouseWheelEvent(static_cast<float>(GET_WHEEL_DELTA_WPARAM(e.WParam)) / WHEEL_DELTA, 0); break;
                case WM_KEYDOWN: case WM_KEYUP: case WM_SYSKEYDOWN: case WM_SYSKEYUP:
                {
                    auto const key = KeyOf(e.WParam);
                    if (key != ImGuiKey_None) io.AddKeyEvent(key, e.Message == WM_KEYDOWN || e.Message == WM_SYSKEYDOWN);
                    break;
                }
                case WM_CHAR:
                    if (e.WParam > 0 && e.WParam < 0x10000) io.AddInputCharacterUTF16(static_cast<unsigned short>(e.WParam));
                    break;
                default: break;
                }
            }

            // No platform backend tells ImGui about focus: without this a button released over another window stays down.
            auto const focused = GetForegroundWindow() == GameWindow();
            if (focused != HadFocus)
            {
                io.AddFocusEvent(focused);
                HadFocus = focused;
            }

            // The position from the cursor itself: the game may not get WM_MOUSEMOVE, and those are swallowed anyway.
            POINT cursor;
            if (GetCursorPos(&cursor) && ScreenToClient(GameWindow(), &cursor))
                io.AddMousePosEvent(static_cast<float>(cursor.x) * scaleX, static_cast<float>(cursor.y) * scaleY);
        }

        // ! Windows
        // ========================================

        void DrawToolbar(Snapshot const& s, float width)
        {
            ImGui::SetNextWindowPos({ width * 0.5f, 10 }, ImGuiCond_Always, { 0.5f, 0 });
            ImGui::SetNextWindowBgAlpha(0.88f);
            auto const flags = ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoMove
                               | ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoFocusOnAppearing;
            if (ImGui::Begin("##MeleBridgeToolbar", nullptr, flags))
            {
                auto freeCam = s.FreeCam;
                if (ImGui::Checkbox("Free cam", &freeCam)) Post({ .Type = Command::Kind::FreeCam, .On = freeCam });
                ImGui::SameLine();
                ImGui::BeginDisabled();
                ImGui::Button("Props");
                ImGui::SameLine();
                ImGui::Button("Save");
                ImGui::EndDisabled();
                if (ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled)) ImGui::SetTooltip("Next phase: docs/07-ingame-editor.md, section 4");
                ImGui::SameLine();
                ImGui::Checkbox("Diagnostics", &ShowDiagnostics);
                ImGui::SameLine();
                if (ImGui::Button("Close (F8)")) Post({ .Type = Command::Kind::Close });

                if (s.FreeCam)
                    ImGui::TextDisabled("Right mouse: look   WASD: move   Q/E: down/up   Shift: fast   Wheel: speed (%.0f)   Left click: select", s.Speed);
                else
                    ImGui::TextDisabled("Left click: select a prop   Arrows: drag to move it   Free cam: fly around");
            }
            ImGui::End();
        }

        void DrawSelection(Snapshot const& s, float width)
        {
            if (!s.Selected) return;
            ImGui::SetNextWindowPos({ width - 10, 10 }, ImGuiCond_Always, { 1, 0 });
            ImGui::SetNextWindowBgAlpha(0.88f);
            if (ImGui::Begin("Selection", nullptr, ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoMove))
            {
                ImGui::TextUnformatted(s.SelectedPath.c_str());
                ImGui::Text("Class: %s%s", s.SelectedClass.c_str(), s.SelectedStatic ? " (static)" : "");
                ImGui::Text("X %.1f   Y %.1f   Z %.1f", s.SelectedLocation.X, s.SelectedLocation.Y, s.SelectedLocation.Z);
                if (ImGui::Button("Deselect")) Post({ .Type = Command::Kind::Deselect });
            }
            ImGui::End();
        }

        void DrawDiagnostics(Snapshot const& s, float width, float height)
        {
            if (!ShowDiagnostics) return;
            ImGui::SetNextWindowPos({ 10, height - 10 }, ImGuiCond_FirstUseEver, { 0, 1 });
            ImGui::SetNextWindowBgAlpha(0.88f);
            if (ImGui::Begin("Diagnostics", &ShowDiagnostics, ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoSavedSettings))
            {
                ImGui::Text("Back buffer %.0f x %.0f   canvas %.0f x %.0f   game frame %llu", width, height, s.CanvasWidth, s.CanvasHeight, s.Frame);
                ImGui::Text("Threads: present %lu   PostRender %lu   UpdateCamera %lu   window %lu", Diag.PresentThread.load(),
                            Diag.PostRenderThread.load(), Diag.CameraThread.load(), Diag.WindowThread.load());
                ImGui::Text("Hooks: Present1 %s   DirectInput %s   cursor %s   window %s", Diag.Present1Hooked.load() ? "yes" : "no",
                            Diag.DirectInputHooked.load() ? "yes" : "no", Diag.CursorHooked.load() ? "yes" : "no", Diag.WindowHooked.load() ? "yes" : "no");
                ImGui::Text("Held back: %llu messages   %llu device states   %llu device reads   %llu cursor calls", Diag.SwallowedMessages.load(),
                            Diag.ZeroedDeviceStates.load(), Diag.ZeroedDeviceData.load(), Diag.BlockedCursorCalls.load());
                ImGui::Text("Camera: %.0f %.0f %.0f   pitch %.1f yaw %.1f   FOV %.1f", s.Pov.Location.X, s.Pov.Location.Y, s.Pov.Location.Z,
                            static_cast<float>(s.Pov.Rotation.Pitch) * 360.f / 65536.f, static_cast<float>(s.Pov.Rotation.Yaw) * 360.f / 65536.f, s.Pov.FOV);
                ImGui::Text("Last pick: %s", s.LastPick.empty() ? "-" : s.LastPick.c_str());
                ImGui::Checkbox("ImGui demo window", &ShowDemo);
            }
            ImGui::End();
            if (ShowDemo) ImGui::ShowDemoWindow(&ShowDemo);
        }

        void DrawGizmo(Snapshot const& s, float const* view, float const* projection, float width, float height)
        {
            if (!s.Selected)
            {
                ResetGizmo();
                return;
            }
            if (Dragging && DraggedPath != s.SelectedPath) Dragging = false;

            float model[16] = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, s.SelectedLocation.X, s.SelectedLocation.Y, s.SelectedLocation.Z, 1 };
            // While dragging, our own copy leads: the game applies each move a frame or two later.
            if (Dragging) std::memcpy(model, DragModel, sizeof model);
            ImGuizmo::SetOrthographic(false);
            ImGuizmo::SetDrawlist(ImGui::GetBackgroundDrawList());
            ImGuizmo::SetRect(0, 0, width, height);
            ImGuizmo::Manipulate(view, projection, ImGuizmo::TRANSLATE, ImGuizmo::WORLD, model);
            if (ImGuizmo::IsUsing())
            {
                Dragging = true;
                DraggedPath = s.SelectedPath;
                std::memcpy(DragModel, model, sizeof model);
                Post({ .Type = Command::Kind::Move, .X = model[12], .Y = model[13], .Z = model[14] });
            }
            else
            {
                Dragging = false;
            }

            // Our projection of the selection's origin against the engine's own Canvas.Project of it.
            float ox = 0, oy = 0;
            if (!ProjectPoint(view, projection, s.SelectedLocation, width, height, ox, oy) || !s.Projected || s.CanvasWidth <= 0 || s.CanvasHeight <= 0) return;
            auto const cx = s.Projection.X * width / s.CanvasWidth, cy = s.Projection.Y * height / s.CanvasHeight;
            auto const error = std::hypot(ox - cx, oy - cy);
            SetCalibration({ true, ox, oy, cx, cy, error });

            auto const draw = ImGui::GetBackgroundDrawList();
            draw->AddCircle({ cx, cy }, 9, IM_COL32(0, 230, 255, 255), 0, 2);
            draw->AddLine({ ox - 6, oy }, { ox + 6, oy }, IM_COL32(255, 220, 0, 255), 2);
            draw->AddLine({ ox, oy - 6 }, { ox, oy + 6 }, IM_COL32(255, 220, 0, 255), 2);
            if (ShowDiagnostics)
            {
                ImGui::SetNextWindowPos({ 10, 10 }, ImGuiCond_Always);
                ImGui::SetNextWindowBgAlpha(0.7f);
                if (ImGui::Begin("##Calibration", nullptr, ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoInputs))
                {
                    ImGui::Text("Projection check: ours (yellow +) %.1f, %.1f   Canvas.Project (cyan o) %.1f, %.1f   error %.1f px", ox, oy, cx, cy, error);
                }
                ImGui::End();
            }

            LARGE_INTEGER now, frequency;
            QueryPerformanceCounter(&now);
            QueryPerformanceFrequency(&frequency);
            if (now.QuadPart - LastCalibrationLog.QuadPart > 2 * frequency.QuadPart)
            {
                LastCalibrationLog = now;
                LEASI_INFO("editor: projection check ours ({:.1f}, {:.1f}) Canvas.Project ({:.1f}, {:.1f}) error {:.1f} px, FOV {:.1f}, {}x{}", ox, oy,
                           cx, cy, error, s.Pov.FOV, width, height);
            }
        }

        /// Clicks and the wheel that no window or arrow took.
        void HandleWorldInput(Snapshot const& s, float width, float height)
        {
            auto const& io = ImGui::GetIO();
            auto const free = !io.WantCaptureMouse && !ImGuizmo::IsOver() && !ImGuizmo::IsUsing();

            if (s.FreeCam && free && ImGui::IsMouseClicked(ImGuiMouseButton_Right))
            {
                // The look is the cursor's travel away from the anchor each frame: anchored mid-window, a click near an edge can
                // still look that way (the cursor is clipped to the window).
                RECT client{};
                POINT centre{};
                GetCursorPos(&LookStart);
                if (GetClientRect(GameWindow(), &client))
                {
                    centre = { (client.left + client.right) / 2, (client.top + client.bottom) / 2 };
                    ClientToScreen(GameWindow(), &centre);
                }
                else
                {
                    centre = LookStart;
                }
                MoveCursor(centre.x, centre.y);
                AnchorX.store(centre.x);
                AnchorY.store(centre.y);
                Looking.store(true);
            }
            if (Looking.load() && (!s.FreeCam || !ImGui::IsMouseDown(ImGuiMouseButton_Right)))
            {
                Looking.store(false);
                MoveCursor(LookStart.x, LookStart.y);
            }
            if (Looking.load()) ImGui::SetMouseCursor(ImGuiMouseCursor_None);

            if (free && !Looking.load() && ImGui::IsMouseClicked(ImGuiMouseButton_Left) && s.CanvasWidth > 0)
            {
                Post({ .Type = Command::Kind::Pick, .X = io.MousePos.x * s.CanvasWidth / width, .Y = io.MousePos.y * s.CanvasHeight / height });
            }
            if (s.FreeCam && free && io.MouseWheel != 0) Post({ .Type = Command::Kind::Speed, .X = io.MouseWheel });
        }

        // ! Frame
        // ========================================

        bool Initialize(IDXGISwapChain* chain)
        {
            DXGI_SWAP_CHAIN_DESC desc{};
            if (FAILED(chain->GetDesc(&desc)) || desc.OutputWindow == nullptr) return false;
            if (FAILED(chain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&Device))) || Device == nullptr) return false;
            Device->GetImmediateContext(&Context);

            IMGUI_CHECKVERSION();
            ImGui::CreateContext();
            auto& io = ImGui::GetIO();
            // imgui.ini would land next to the game's executable, inside the install.
            io.IniFilename = nullptr;
            io.LogFilename = nullptr;
            io.MouseDrawCursor = true;
            io.ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;
            io.BackendPlatformName = "MeleBridge";
            if (GetFileAttributesW(L"C:\\Windows\\Fonts\\segoeui.ttf") != INVALID_FILE_ATTRIBUTES)
                io.Fonts->AddFontFromFileTTF("C:\\Windows\\Fonts\\segoeui.ttf", 18.f);
            else
                io.Fonts->AddFontDefault();
            ImGui::StyleColorsDark();
            ImGui::GetStyle().WindowRounding = 6;
            ImGui::GetStyle().FrameRounding = 4;
            if (!ImGui_ImplDX11_Init(Device, Context)) return false;

            GameChain = chain;
            QueryPerformanceCounter(&LastFrame);
            SubclassWindow(desc.OutputWindow);
            LEASI_INFO("editor: overlay ready, back buffer {}x{} format {}, present thread {}", desc.BufferDesc.Width, desc.BufferDesc.Height,
                       static_cast<int>(desc.BufferDesc.Format), GetCurrentThreadId());
            return true;
        }

        bool EnsureTarget(IDXGISwapChain* chain)
        {
            if (Target != nullptr) return true;
            ID3D11Texture2D* back = nullptr;
            if (FAILED(chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&back))) || back == nullptr) return false;
            auto hr = Device->CreateRenderTargetView(back, nullptr, &Target);
            if (FAILED(hr))
            {
                // A typeless back buffer needs a format for its view.
                D3D11_RENDER_TARGET_VIEW_DESC view{};
                view.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
                view.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
                hr = Device->CreateRenderTargetView(back, &view, &Target);
                if (FAILED(hr)) LEASI_ERROR("editor: no render target view on the back buffer (0x{:08X})", static_cast<unsigned>(hr));
            }
            back->Release();
            return Target != nullptr;
        }

        void Render(IDXGISwapChain* chain)
        {
            if (!EnsureTarget(chain)) return;
            ID3D11RenderTargetView* saved[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT]{};
            ID3D11DepthStencilView* savedDepth = nullptr;
            Context->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, saved, &savedDepth);
            Context->OMSetRenderTargets(1, &Target, nullptr);
            ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
            Context->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, saved, savedDepth);
            for (auto view : saved)
            {
                if (view != nullptr) view->Release();
            }
            if (savedDepth != nullptr) savedDepth->Release();
        }

        void Frame(IDXGISwapChain* chain)
        {
            Diag.Presents.fetch_add(1, std::memory_order_relaxed);
            Diag.PresentThread.store(GetCurrentThreadId(), std::memory_order_relaxed);
            if (Failed) return;
            if (!Ready)
            {
                Ready = Initialize(chain);
                if (!Ready)
                {
                    Failed = true;
                    Diag.OverlayFailed.store(true);
                    LEASI_ERROR("editor: the overlay could not start on this swap chain");
                    return;
                }
                Diag.OverlayReady.store(true);
            }
            if (chain != GameChain) return;

            if (!Open.load(std::memory_order_acquire))
            {
                if (WasOpen)
                {
                    // Nothing held from the last session: no button stays down, no event replays.
                    WasOpen = false;
                    DrainInput();
                    ImGui::GetIO().ClearInputKeys();
                    ImGui::GetIO().ClearInputMouse();
                    WantMouse.store(false);
                    WantKeyboard.store(false);
                    ResetGizmo();
                }
                return;
            }
            WasOpen = true;

            DXGI_SWAP_CHAIN_DESC desc{};
            chain->GetDesc(&desc);
            auto const width = static_cast<float>(desc.BufferDesc.Width), height = static_cast<float>(desc.BufferDesc.Height);
            Diag.BackbufferWidth.store(static_cast<int>(desc.BufferDesc.Width));
            Diag.BackbufferHeight.store(static_cast<int>(desc.BufferDesc.Height));
            if (width <= 0 || height <= 0) return;

            auto& io = ImGui::GetIO();
            RECT client{};
            GetClientRect(GameWindow(), &client);
            auto const clientWidth = static_cast<float>(client.right - client.left), clientHeight = static_cast<float>(client.bottom - client.top);
            FeedInput(io, clientWidth > 0 ? width / clientWidth : 1, clientHeight > 0 ? height / clientHeight : 1);

            LARGE_INTEGER now, frequency;
            QueryPerformanceCounter(&now);
            QueryPerformanceFrequency(&frequency);
            io.DeltaTime = std::max(static_cast<float>(now.QuadPart - LastFrame.QuadPart) / static_cast<float>(frequency.QuadPart), 1e-4f);
            LastFrame = now;
            io.DisplaySize = { width, height };

            ImGui_ImplDX11_NewFrame();
            ImGui::NewFrame();
            ImGuizmo::BeginFrame();

            auto const s = Latest();
            float view[16], projection[16];
            ViewMatrix(s.Pov, view);
            ProjectionMatrix(s.Pov.FOV > 1 ? s.Pov.FOV : 70, width, height, projection);

            DrawToolbar(s, width);
            DrawSelection(s, width);
            DrawDiagnostics(s, width, height);
            DrawGizmo(s, view, projection, width, height);
            HandleWorldInput(s, width, height);

            ImGui::Render();
            Render(chain);
            WantMouse.store(io.WantCaptureMouse);
            WantKeyboard.store(io.WantTextInput);
            Diag.OverlayFrames.fetch_add(1, std::memory_order_relaxed);
        }

        void FrameEntry(IDXGISwapChain* chain)
        {
            try
            {
                Frame(chain);
            }
            catch (std::exception const& e)
            {
                Failed = true;
                Diag.OverlayFailed.store(true);
                LEASI_ERROR("editor overlay: {}", e.what());
            }
        }

        /// An access violation in the overlay turns the overlay off, not the game. No C++ object may live in this frame.
        void Guarded(IDXGISwapChain* chain)
        {
            __try
            {
                FrameEntry(chain);
            }
            __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH)
            {
                Failed = true;
                Diag.OverlayFailed.store(true);
            }
        }

        // The guard spans the original call too: an overlay or runtime between us and DXGI may present one way through the
        // other (Present1 into Present, or back), and the frame must be drawn once.
        HRESULT STDMETHODCALLTYPE Present_hook(IDXGISwapChain* chain, UINT sync, UINT flags)
        {
            auto const orig = Wait(Present_orig);
            if (InPresent) return orig(chain, sync, flags);
            InPresent = true;
            // DXGI_PRESENT_TEST only asks whether presenting would work: nothing is shown.
            if (!(flags & DXGI_PRESENT_TEST)) Guarded(chain);
            auto const hr = orig(chain, sync, flags);
            InPresent = false;
            return hr;
        }

        HRESULT STDMETHODCALLTYPE Present1_hook(IDXGISwapChain1* chain, UINT sync, UINT flags, DXGI_PRESENT_PARAMETERS const* parameters)
        {
            auto const orig = Wait(Present1_orig);
            if (InPresent) return orig(chain, sync, flags, parameters);
            InPresent = true;
            if (!(flags & DXGI_PRESENT_TEST)) Guarded(chain);
            auto const hr = orig(chain, sync, flags, parameters);
            InPresent = false;
            return hr;
        }

        HRESULT STDMETHODCALLTYPE ResizeBuffers_hook(IDXGISwapChain* chain, UINT count, UINT width, UINT height, DXGI_FORMAT format, UINT flags)
        {
            auto const orig = Wait(ResizeBuffers_orig);
            // The view holds the old back buffer, which must be released before it can be resized.
            if (chain == GameChain && Target != nullptr)
            {
                Target->Release();
                Target = nullptr;
            }
            return orig(chain, count, width, height, format, flags);
        }
    }

    void InstallOverlay(::LESDK::Initializer& Init)
    {
        // The swap chain methods' addresses, from a throwaway device on a hidden window: every swap chain shares them.
        WNDCLASSEXW windowClass{};
        windowClass.cbSize = sizeof windowClass;
        windowClass.lpfnWndProc = DefWindowProcW;
        windowClass.hInstance = GetModuleHandleW(nullptr);
        windowClass.lpszClassName = L"MeleBridgeProbe";
        RegisterClassExW(&windowClass);
        auto const window = CreateWindowExW(0, windowClass.lpszClassName, L"", WS_OVERLAPPEDWINDOW, 0, 0, 16, 16, nullptr, nullptr, windowClass.hInstance, nullptr);

        DXGI_SWAP_CHAIN_DESC desc{};
        desc.BufferCount = 1;
        desc.BufferDesc.Width = 16;
        desc.BufferDesc.Height = 16;
        desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.OutputWindow = window;
        desc.SampleDesc.Count = 1;
        desc.Windowed = TRUE;
        desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        void* present = nullptr;
        void* present1 = nullptr;
        void* resize = nullptr;
        IDXGISwapChain* chain = nullptr;
        ID3D11Device* device = nullptr;
        ID3D11DeviceContext* context = nullptr;
        auto hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &desc, &chain, &device, nullptr, &context);
        if (FAILED(hr)) hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &desc, &chain, &device, nullptr, &context);
        if (SUCCEEDED(hr) && chain != nullptr)
        {
            // IDXGISwapChain: Present is 8, ResizeBuffers 13; IDXGISwapChain1 adds Present1 at 22.
            auto const table = *reinterpret_cast<void***>(chain);
            present = table[8];
            resize = table[13];
            IDXGISwapChain1* chain1 = nullptr;
            if (SUCCEEDED(chain->QueryInterface(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&chain1))) && chain1 != nullptr)
            {
                present1 = (*reinterpret_cast<void***>(chain1))[22];
                chain1->Release();
            }
        }
        if (context != nullptr) context->Release();
        if (chain != nullptr) chain->Release();
        if (device != nullptr) device->Release();
        if (window != nullptr) DestroyWindow(window);
        UnregisterClassW(windowClass.lpszClassName, windowClass.hInstance);

        if (present == nullptr || resize == nullptr)
        {
            LEASI_ERROR("editor: no D3D11 device to read the swap chain methods from (0x{:08X}), overlay off", static_cast<unsigned>(hr));
            return;
        }
        Present_orig.store(static_cast<t_Present*>(Init.InstallHook("MeleBridge IDXGISwapChain::Present", present, Present_hook)));
        ResizeBuffers_orig.store(static_cast<t_ResizeBuffers*>(Init.InstallHook("MeleBridge IDXGISwapChain::ResizeBuffers", resize, ResizeBuffers_hook)));
        if (present1 != nullptr && present1 != present)
            Present1_orig.store(static_cast<t_Present1*>(Init.InstallHook("MeleBridge IDXGISwapChain1::Present1", present1, Present1_hook)));
        Diag.PresentHooked.store(Present_orig.load() != nullptr);
        Diag.ResizeHooked.store(ResizeBuffers_orig.load() != nullptr);
        Diag.Present1Hooked.store(Present1_orig.load() != nullptr);
        LEASI_INFO("editor: swap chain hooks Present {} ResizeBuffers {} Present1 {}", Diag.PresentHooked.load(), Diag.ResizeHooked.load(),
                   Diag.Present1Hooked.load());
    }
}
