#include "MeleBridge/Editor.hpp"

#include <d3d11.h>
#include <dxgi1_2.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <cmath>
#include <algorithm>
#include <cctype>
#include <cstring>
#include <filesystem>
#include <format>
#include <fstream>
#include <future>

#pragma warning(push, 0)
#include "imgui.h"
#include "backends/imgui_impl_dx11.h"
#include "ImGuizmo.h"
#pragma warning(pop)

// The editor's render-thread half: Dear ImGui and ImGuizmo drawn into the game's back buffer from a hooked
// IDXGISwapChain::Present. It never touches game objects: it draws from the game thread's snapshot and posts commands back.
// It also reads the prop catalog mele-mcp builds (catalog.json and its thumbnail PNGs) for the Props window's grid, and draws
// the lights and the effects the editor lists (icons, the selected light's radius, its panel).
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

        enum class Tool { Move, Turn, Scale };
        Tool Current = Tool::Move;
        Tool LastTool = Tool::Move;
        /// A pawn's turn, in degrees: the selection's yaw at the start of the drag plus each frame's turn.
        float DragYaw = 0;
        /// The selection at the start of the drag: a scale changes only the dragged axis, a pawn's turn only the yaw.
        FRotator DragRotation{};
        float DragDrawScale = 1;
        FVector DragScale3D{ 1, 1, 1 };
        /// The handle the drag holds: ImGuizmo forgets it on the frame the button is released, whose change still counts.
        ImGuizmo::MOVETYPE DragHandle = ImGuizmo::MT_NONE;
        /// The overlay's own refusals (a tool the selection can't take), shown like the game thread's status line.
        std::string Refusal;
        LARGE_INTEGER RefusalSince{};
        /// The selection and tool the gizmo last refused, so it is said once, not every frame.
        std::string RefusedFor;
        bool ShowProps = false;
        bool MeshesAsked = false;
        char Filter[128]{};
        /// The Props list's rows for the filter and mesh list they were made from. Holding the list keeps its address from being
        /// reused by a newer one, which would leave the rows stale.
        std::vector<int> Shown;
        std::string ShownFilter;
        std::shared_ptr<std::vector<std::string> const> ShownFrom;
        /// When the game thread's last status line arrived, to show it for a few seconds.
        unsigned long long StatusSeen = 0;
        LARGE_INTEGER StatusSince{};
        /// The Lights or the Effects tab is open this frame: a click near one of that tab's icons selects it, as with Alt held
        /// (docs/07 §12).
        bool LightsTabOpen = false;
        bool EffectsTabOpen = false;
        /// The light panel's settings while a control is held, and for two game frames after the last change: the game thread
        /// applies a change a frame or two later, and the snapshot would pull the control back meanwhile.
        LightBlock PanelLight;
        std::string PanelPath;
        bool PanelEditing = false;
        unsigned long long PanelFrame = 0;

        /// Light and effect icons are this big; a click this close to one selects its light or effect.
        constexpr float IconRadius = 7;
        constexpr float IconPickPixels = 12;
        /// Untouched effects get an icon within 30 m of the camera, at most 50 of them, nearest first (docs/07 §12).
        constexpr float EffectIconReach = 3000;
        constexpr size_t MaxEffectIcons = 50;
        /// The panel's ranges, as the game thread holds a Light command to them.
        constexpr float MinRadius = 16, MaxRadius = 4096, MaxBrightness = 10, MinFalloff = 1, MaxFalloff = 8;

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

        /// FRotationMatrix's rows: a rotation's forward (X), right (Y) and up (Z) axes in world space.
        void Axes(FRotator const& rotation, float* forward, float* right, float* up)
        {
            auto const p = static_cast<float>(rotation.Pitch) * UnitsToRadians;
            auto const y = static_cast<float>(rotation.Yaw) * UnitsToRadians;
            auto const r = static_cast<float>(rotation.Roll) * UnitsToRadians;
            auto const sp = std::sin(p), cp = std::cos(p), sy = std::sin(y), cy = std::cos(y), sr = std::sin(r), cr = std::cos(r);
            float const f[3] = { cp * cy, cp * sy, sp };
            float const g[3] = { sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp };
            float const u[3] = { -(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp };
            std::memcpy(forward, f, sizeof f);
            std::memcpy(right, g, sizeof g);
            std::memcpy(up, u, sizeof u);
        }

        /// An actor's transform as UE3's local-to-world matrix: the axes as rows, each scaled by DrawScale times its
        /// DrawScale3D component, then the location.
        void ModelMatrix(FVector location, FRotator const& rotation, float drawScale, FVector scale3d, float* m)
        {
            Axes(rotation, m, m + 4, m + 8);
            float const scale[3] = { drawScale * scale3d.X, drawScale * scale3d.Y, drawScale * scale3d.Z };
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++) m[row * 4 + column] *= scale[row];
            }
            m[3] = m[7] = m[11] = 0;
            m[12] = location.X;
            m[13] = location.Y;
            m[14] = location.Z;
            m[15] = 1;
        }

        /// The rotation of a model matrix, as UE3's FMatrix::Rotator reads it from the normalised rows: pitch and yaw from the
        /// forward axis, roll from the up and right axes against the right axis with no roll. In double: a float atan2 near the
        /// poles is off by whole units.
        FRotator RotatorOf(float const* m)
        {
            constexpr double Units = 32768.0 / 3.14159265358979323846;
            double axis[3][3];
            for (int row = 0; row < 3; row++)
            {
                auto const* r = m + row * 4;
                auto const length = std::sqrt(double(r[0]) * r[0] + double(r[1]) * r[1] + double(r[2]) * r[2]);
                for (int i = 0; i < 3; i++) axis[row][i] = length > 0 ? r[i] / length : 0;
            }
            auto const& x = axis[0];
            auto const& y = axis[1];
            auto const& z = axis[2];
            FRotator out{ static_cast<int>(std::lround(std::atan2(x[2], std::sqrt(x[0] * x[0] + x[1] * x[1])) * Units)),
                          static_cast<int>(std::lround(std::atan2(x[1], x[0]) * Units)), 0 };
            auto const yaw = out.Yaw / Units;
            double const unrolled[3] = { -std::sin(yaw), std::cos(yaw), 0 };
            auto const along = [&](double const* a) { return a[0] * unrolled[0] + a[1] * unrolled[1] + a[2] * unrolled[2]; };
            out.Roll = static_cast<int>(std::lround(std::atan2(along(z), along(y)) * Units));
            return out;
        }

        /// UE3's view: world → camera space with x right, y up, z forward (depth), as rows for row vectors (v * M), which is
        /// both FMatrix's and ImGuizmo's layout. The axes are FRotationMatrix's rows.
        void ViewMatrix(FTPOV const& pov, float* m)
        {
            float forward[3], right[3], up[3];
            Axes(pov.Rotation, forward, right, up);
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
        /// on and IsOver/IsUsing would keep every click from reaching the world. A drag cut short still ends its undo step.
        void ResetGizmo()
        {
            if (Dragging) Post({ .Type = Command::Kind::EndDrag });
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

        // ! Tools
        // ========================================

        /// A negative (or zero) DrawScale or DrawScale3D component: the gizmo's matrix is then left-handed, and neither a
        /// rotation nor a scale can be read back from it.
        bool Mirrored(Snapshot const& s)
        {
            return s.SelectedDrawScale <= 0 || s.SelectedDrawScale3D.X <= 0 || s.SelectedDrawScale3D.Y <= 0 || s.SelectedDrawScale3D.Z <= 0;
        }

        /// Why the editor won't change the selection with any tool (a vanilla door, a room shell: docs/07 §11), or empty. Not a
        /// reason to keep a tool from being chosen: the next selection may take it.
        std::string ActorRefusal(Snapshot const& s, Tool tool)
        {
            if (!s.Selected || s.SelectedUnmovable.empty()) return {};
            return std::format("not {}: {}", tool == Tool::Move ? "moved" : tool == Tool::Turn ? "turned" : "scaled", s.SelectedUnmovable);
        }

        /// Why a tool can't take the selection, or empty.
        std::string ToolRefusal(Snapshot const& s, Tool tool)
        {
            if (!s.Selected || tool == Tool::Move) return {};
            auto const verb = tool == Tool::Turn ? "turned" : "scaled";
            if (s.SelectedLight)
                return tool == Tool::Turn ? "not turned: a light only moves (a point light shines all ways)" : "not scaled: a light only moves (its radius is in the panel)";
            if (tool == Tool::Scale && s.SelectedPawn) return "not scaled: npcs keep a yaw and no scale";
            if (tool == Tool::Scale && s.SelectedEffect) return "not scaled: an effect moves and turns (how its particles follow a scale is unchecked)";
            if (Mirrored(s))
                return std::format("not {}: {} is mirrored (DrawScale {:g}, DrawScale3D {:g} {:g} {:g}); move it, or fix its scale in the YAML",
                                   verb, s.SelectedPath.substr(s.SelectedPath.rfind('.') + 1), s.SelectedDrawScale, s.SelectedDrawScale3D.X,
                                   s.SelectedDrawScale3D.Y, s.SelectedDrawScale3D.Z);
            return {};
        }

        void Refuse(std::string text)
        {
            LEASI_INFO("editor: {}", text);
            Refusal = std::move(text);
            QueryPerformanceCounter(&RefusalSince);
        }

        /// T, R, G and the toolbar's radios: the tool, unless the selection can't take it (then the status line says why).
        void Choose(Snapshot const& s, Tool tool)
        {
            if (auto why = ToolRefusal(s, tool); !why.empty())
            {
                Refuse(std::move(why));
                return;
            }
            Current = tool;
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
                ImGui::Checkbox("Props", &ShowProps);
                ImGui::SameLine();
                auto autoLight = s.AutoLight;
                if (ImGui::Checkbox("Auto light", &autoLight)) Post({ .Type = Command::Kind::AutoLight, .On = autoLight });
                if (ImGui::IsItemHovered())
                    ImGui::SetTooltip("A fresh spawn that gets under 60%% of its target from prop lights gets a prop light of its own: one Ctrl+Z "
                                      "removes both, and it follows the prop until you save or move or set the light. L adds one for the "
                                      "selected prop either way. Neither adds one for a prop that needs a radius over %.0f.",
                                      MaxAutoRadius);
                ImGui::SameLine();
                ImGui::TextDisabled("|");
                ImGui::SameLine();
                // Not mid-drag: the gizmo would change under the mouse.
                auto const dragging = ImGuizmo::IsUsingAny();
                ImGui::BeginDisabled(dragging);
                if (ImGui::RadioButton("Move (T)", Current == Tool::Move)) Choose(s, Tool::Move);
                ImGui::SameLine();
                if (ImGui::RadioButton("Turn (R)", Current == Tool::Turn)) Choose(s, Tool::Turn);
                if (s.Selected && s.SelectedPawn && ImGui::IsItemHovered()) ImGui::SetTooltip("npcs turn about the vertical only: the level YAML keeps a yaw");
                ImGui::SameLine();
                ImGui::BeginDisabled(s.Selected && s.SelectedPawn);
                if (ImGui::RadioButton("Scale (G)", Current == Tool::Scale)) Choose(s, Tool::Scale);
                ImGui::EndDisabled();
                if (s.Selected && s.SelectedPawn && ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled))
                    ImGui::SetTooltip("npcs keep a yaw and no scale");
                ImGui::EndDisabled();
                ImGui::SameLine();
                ImGui::TextDisabled("|");
                ImGui::SameLine();
                ImGui::BeginDisabled(!s.Selected || !s.SelectedUndeletable.empty() || dragging);
                if (ImGui::Button("Delete")) Post({ .Type = Command::Kind::Delete });
                ImGui::EndDisabled();
                if (s.Selected && !s.SelectedUndeletable.empty() && ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled))
                    ImGui::SetTooltip("%s", s.SelectedUndeletable.c_str());
                ImGui::SameLine();
                // Named by the game thread: what the step is and whose ("move ed_crate_x_1").
                ImGui::BeginDisabled(s.UndoLabel.empty() || dragging);
                if (ImGui::Button("Undo")) Post({ .Type = Command::Kind::Undo });
                ImGui::EndDisabled();
                if (ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled))
                    ImGui::SetTooltip("%s", s.UndoLabel.empty() ? "Nothing to undo" : std::format("Undo {} (Ctrl+Z)", s.UndoLabel).c_str());
                ImGui::SameLine();
                ImGui::BeginDisabled(s.RedoLabel.empty() || dragging);
                if (ImGui::Button("Redo")) Post({ .Type = Command::Kind::Redo });
                ImGui::EndDisabled();
                if (ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled))
                    ImGui::SetTooltip("%s", s.RedoLabel.empty() ? "Nothing to redo" : std::format("Redo {} (Ctrl+Y)", s.RedoLabel).c_str());
                ImGui::SameLine();
                auto const saveLabel = s.Unsaved > 0 ? std::format("Save ({})###Save", s.Unsaved) : std::string("Save###Save");
                if (ImGui::Button(saveLabel.c_str())) Post({ .Type = Command::Kind::Save });
                if (ImGui::IsItemHovered()) ImGui::SetTooltip("Ctrl+S. Writes the session file; editor_import in mele-mcp puts it in the level YAML and builds.");
                // Until a save writes every map (why: the line under the hints): the status line fades, and the first pass after a
                // load replaces it.
                if (!s.SaveProblem.empty())
                {
                    ImGui::SameLine();
                    ImGui::TextColored(ImVec4(1, 0.6f, 0.3f, 1), "not saved");
                }
                ImGui::SameLine();
                ImGui::TextDisabled("|");
                ImGui::SameLine();
                ImGui::Checkbox("Diagnostics", &ShowDiagnostics);
                ImGui::SameLine();
                if (ImGui::Button("Close (F8)")) Post({ .Type = Command::Kind::Close });

                if (s.FreeCam)
                    ImGui::TextDisabled("Right mouse: look   WASD: move   Q/E: down/up   Shift: fast   Wheel: speed (%.0f)   Left click: select", s.Speed);
                else
                    ImGui::TextDisabled("Left click: select   T/R/G: move, turn, scale   Ctrl: snap from where the drag started (10 units, 15 "
                                        "degrees, scale by 0.1)   Del: delete   Ctrl+Z/Y: undo/redo");
                ImGui::TextDisabled("L: a prop light for the selected prop   Alt+click: the icon of a light or an effect nearest the camera "
                                    "(the Lights or Effects tab open: that tab's)");
                if (!s.SaveProblem.empty()) ImGui::TextColored(ImVec4(1, 0.6f, 0.3f, 1), "Last save: %s", s.SaveProblem.c_str());

                // The last word, the game thread's or the overlay's own, for a few seconds.
                LARGE_INTEGER now, frequency;
                QueryPerformanceCounter(&now);
                QueryPerformanceFrequency(&frequency);
                if (s.StatusFrame != StatusSeen)
                {
                    StatusSeen = s.StatusFrame;
                    StatusSince = now;
                }
                auto const ours = !Refusal.empty() && RefusalSince.QuadPart >= StatusSince.QuadPart;
                auto const& status = ours ? Refusal : s.Status;
                auto const since = ours ? RefusalSince : StatusSince;
                if (!status.empty() && now.QuadPart - since.QuadPart < 8 * frequency.QuadPart)
                {
                    auto const refused = status.starts_with("not ") || status.starts_with("the engine refused");
                    ImGui::TextColored(refused ? ImVec4(1, 0.6f, 0.3f, 1) : ImVec4(0.5f, 1, 0.6f, 1), "%s", status.c_str());
                }
            }
            ImGui::End();
        }

        /// The selected light's panel (docs/07 §11): every change sends all of its settings, held to the panel's ranges. A drag
        /// on a control is one undo step: EndPanel closes it once no control is held. An NPC's light (§12) has no kind control
        /// (it keeps its template's channels) and shows its offset from its NPC. A light the editor won't change now (its NPC
        /// fails the pose check) shows its settings greyed.
        void DrawLightPanel(Snapshot const& s)
        {
            if (PanelPath != s.SelectedPath || (!PanelEditing && s.Frame > PanelFrame + 1))
            {
                PanelLight = s.SelectedLightBlock;
                PanelPath = s.SelectedPath;
            }
            auto light = PanelLight;
            auto changed = false;
            auto const clamped = ImGuiSliderFlags_AlwaysClamp;
            auto const npc = light.Kind == LightKind::Npc;
            // The controls are the selected light's own: a selection that changes while one is held (an undo, a pass) ends its drag
            // instead of carrying it to the next light.
            ImGui::PushID(s.SelectedPath.c_str());
            ImGui::BeginDisabled(!s.SelectedUnmovable.empty());
            if (npc)
            {
                ImGui::SeparatorText(std::format("NPC light: {}", s.SelectedNpcName.empty() ? s.SelectedNpcTag : s.SelectedNpcName).c_str());
                // Rounded as the YAML writes it: the offset is computed in floats. Adding 0 turns a -0 into 0.
                auto const shown = [](float v) { return std::round(v * 100.f) / 100.f + 0.f; };
                ImGui::Text("Offset [forward, right, up]: [%g, %g, %g]", shown(s.SelectedOffset.X), shown(s.SelectedOffset.Y), shown(s.SelectedOffset.Z));
                if (ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled))
                    ImGui::SetTooltip("From %s's centre (the pawn's Location), turned by its yaw: move the light to change it. Moving or turning "
                                      "the NPC takes its lights along.",
                                      s.SelectedNpcName.empty() ? s.SelectedNpcTag.c_str() : s.SelectedNpcName.c_str());
                ImGui::PushTextWrapPos(ImGui::GetCursorPosX() + 360);
                ImGui::TextDisabled("It keeps its template's channels (Dynamic: characters and props on their light environment, not the walls).");
                ImGui::PopTextWrapPos();
            }
            else
            {
                ImGui::SeparatorText(light.Kind == LightKind::Room ? "Room light" : "Prop light");
                if (ImGui::RadioButton("Prop light", light.Kind == LightKind::Props))
                {
                    light.Kind = LightKind::Props;
                    changed = true;
                }
                if (ImGui::IsItemHovered()) ImGui::SetTooltip("Gameplay_4 only: lights our props, never walls, pawns or vanilla meshes.");
                ImGui::SameLine();
                if (ImGui::RadioButton("Room light", light.Kind == LightKind::Room))
                {
                    light.Kind = LightKind::Room;
                    changed = true;
                }
                if (ImGui::IsItemHovered()) ImGui::SetTooltip("Walls, vanilla meshes, pawns and props alike: a match made with a prop light survives it.");
                ImGui::SameLine();
            }
            changed |= ImGui::Checkbox("Enabled", &light.Enabled);
            ImGui::SetNextItemWidth(240);
            changed |= ImGui::SliderFloat("Radius", &light.Radius, MinRadius, MaxRadius, "%.0f", ImGuiSliderFlags_Logarithmic | clamped);
            ImGui::SetNextItemWidth(240);
            changed |= ImGui::SliderFloat("Brightness", &light.Brightness, 0, MaxBrightness, "%.3f", ImGuiSliderFlags_Logarithmic | clamped);
            ImGui::SetNextItemWidth(240);
            float colour[3] = { static_cast<float>(light.Color[0]) / 255.f, static_cast<float>(light.Color[1]) / 255.f, static_cast<float>(light.Color[2]) / 255.f };
            if (ImGui::ColorEdit3("Colour", colour, ImGuiColorEditFlags_Uint8 | ImGuiColorEditFlags_DisplayRGB))
            {
                for (size_t i = 0; i < 3; i++) light.Color[i] = std::clamp(static_cast<int>(std::lround(colour[i] * 255.f)), 0, 255);
                changed = true;
            }
            ImGui::SetNextItemWidth(240);
            changed |= ImGui::SliderFloat("Falloff", &light.Falloff, MinFalloff, MaxFalloff, "%.2f", clamped);
            // The prop-coverage rule is for prop lights only: an NPC light is never on their channel.
            if (ImGui::IsItemHovered())
                ImGui::SetTooltip("%s", npc ? "FalloffExponent: 2 is a point light's usual."
                                            : "FalloffExponent: 2 is a point light's usual. A prop is covered where (1 - (d/R)^2)^falloff >= 0.25.");
            ImGui::EndDisabled();
            ImGui::PopID();
            if (changed && !(light == PanelLight))
            {
                PanelLight = light;
                PanelEditing = true;
                PanelFrame = s.Frame;
                Post({ .Type = Command::Kind::Light, .Light = light });
            }
        }

        /// The panel's drag ends when no control is held any more (a checkbox or a radio button: the frame it changed): its undo
        /// step closes, and the paused world ticks once.
        void EndPanel(Snapshot const& s)
        {
            if (!PanelEditing || ImGui::IsAnyItemActive()) return;
            Post({ .Type = Command::Kind::EndDrag });
            PanelEditing = false;
            PanelFrame = s.Frame;
        }

        void DrawSelection(Snapshot const& s, float width)
        {
            if (!s.Selected) return;
            ImGui::SetNextWindowPos({ width - 10, 10 }, ImGuiCond_Always, { 1, 0 });
            ImGui::SetNextWindowBgAlpha(0.88f);
            if (ImGui::Begin("Selection", nullptr, ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoMove))
            {
                ImGui::TextUnformatted(s.SelectedPath.c_str());
                ImGui::Text("Class: %s%s%s", s.SelectedClass.c_str(), s.SelectedStatic ? " (static)" : "", s.SelectedSpawned ? " (spawned)" : "");
                ImGui::Text("Tag: %s", s.SelectedTag.c_str());
                // An effect (docs/07 §12): one that ends shows nothing once the 1.5 s run after a step is over.
                if (s.SelectedEffect)
                {
                    ImGui::Text("Template: %s%s", s.SelectedTemplate.c_str(), s.SelectedTemplate == "no template" ? "" : s.SelectedLoops ? " (loops)" : " (ends)");
                    if (ImGui::IsItemHovered())
                        ImGui::SetTooltip("loops: it plays on. ends: it plays once; after a move it shows for the 1.5 s run, then nothing until the "
                                          "game starts it again.");
                }
                // A vanilla actor: whether its edits ship (docs/07 §11). An actor the editor won't move: why.
                if (!s.SelectedShips.empty())
                {
                    auto const ships = s.SelectedShips.starts_with("ships: ") && !s.SelectedShips.starts_with("ships: unknown");
                    ImGui::TextColored(ships ? ImVec4(0.5f, 1, 0.6f, 1) : ImVec4(1, 0.6f, 0.3f, 1), "%s", s.SelectedShips.c_str());
                }
                if (!s.SelectedUnmovable.empty()) ImGui::TextColored(ImVec4(1, 0.6f, 0.3f, 1), "Not edited: %s", s.SelectedUnmovable.c_str());
                auto const degrees = [](int units) { return static_cast<float>(static_cast<short>(units & 0xFFFF)) * 360.f / 65536.f; };
                ImGui::Text("X %.1f   Y %.1f   Z %.1f", s.SelectedLocation.X, s.SelectedLocation.Y, s.SelectedLocation.Z);
                // A light only moves: its rotation and scales are the actor's, and count for nothing.
                if (!s.SelectedLight)
                {
                    ImGui::Text("Pitch %.1f   Yaw %.1f   Roll %.1f", degrees(s.SelectedRotation.Pitch), degrees(s.SelectedRotation.Yaw),
                                degrees(s.SelectedRotation.Roll));
                    ImGui::Text("DrawScale %g   DrawScale3D %g %g %g", s.SelectedDrawScale, s.SelectedDrawScale3D.X, s.SelectedDrawScale3D.Y,
                                s.SelectedDrawScale3D.Z);
                }
                // A prop the editor switches (docs/07 §11): what prop lights give it (E) against what a fresh Auto light would (T).
                if (s.SelectedProp)
                {
                    // Too big for one prop light: L would refuse it, so nothing here may offer L (the game thread makes its target
                    // nothing).
                    auto const tooBig = s.SelectedAutoRadius > MaxAutoRadius;
                    if (tooBig)
                    {
                        ImGui::TextColored(ImVec4(1, 0.6f, 0.3f, 1), "too big for one prop light: it needs radius %.0f from here, over %.0f (L adds none)",
                                           s.SelectedAutoRadius, MaxAutoRadius);
                        // Covered, a light already lights it (its Auto light kept on a side that fits, one placed by hand).
                        if (!s.SelectedCovered) ImGui::TextDisabled("a room light (Lights tab) lights it with the walls around it");
                    }
                    // No target: a fresh prop light would give nothing here, so no share says anything.
                    else if (s.SelectedTarget <= 0)
                    {
                        ImGui::TextDisabled("prop light: no target");
                    }
                    else
                    {
                        auto const share = s.SelectedReceived / s.SelectedTarget;
                        auto const low = share < 0.6f;
                        ImGui::TextColored(low ? ImVec4(1, 0.6f, 0.3f, 1) : ImVec4(0.5f, 1, 0.6f, 1), "prop light %.0f%% of target%s", share * 100,
                                           low ? ": L adds one" : "");
                    }
                    if (!s.SelectedCovered)
                        ImGui::TextColored(ImVec4(1, 0.6f, 0.3f, 1), "no prop light covers it: lit by its light environment, brighter than vanilla%s",
                                           tooBig ? "" : " (L)");
                }
                if (s.SelectedLight) DrawLightPanel(s);
                if (ImGui::Button("Deselect")) Post({ .Type = Command::Kind::Deselect });
            }
            ImGui::End();
        }

        bool ContainsNoCase(std::string const& text, std::string const& part)
        {
            return std::search(text.begin(), text.end(), part.begin(), part.end(), [](char a, char b) {
                       return std::tolower(static_cast<unsigned char>(a)) == std::tolower(static_cast<unsigned char>(b));
                   }) != text.end();
        }

        // ! Catalog
        // ========================================

        /// Thumbnails are drawn 96 px in a cell a little wider, the name under them. A few are decoded a frame, as cells show.
        constexpr float ThumbSize = 96;
        constexpr float CellWidth = 112;
        constexpr int ThumbsPerFrame = 4;
        /// They are 128 px; anything past this is not a thumbnail.
        constexpr UINT MaxThumbSide = 1024;

        /// One prop of %LOCALAPPDATA%\MeleBridge\LE1\catalog\catalog.json (mele-mcp's catalog_build writes it).
        struct CatalogEntry
        {
            std::string Name;
            /// Index into Catalog::Categories.
            int Category = 0;
            /// The vanilla path, which is also the path in memory when a level has the mesh.
            std::string Mesh;
            /// A vanilla file (no .pcc) with the mesh as an export: the session's mesh.file, which editor_import ports it from.
            std::string Source;
            /// The catalog package's copy (MeleCatalog_Core.<group>.<name>, a group per category), which DynamicLoadObject reads.
            std::string Load;
            /// The thumbnail PNG's full path; empty when there is none.
            std::wstring ThumbFile;
            float Size[3]{};
            int Tris = 0;
            /// How often the vanilla levels place it.
            int Placed = 0;
            /// Bigger than a prop usually is.
            bool Large = false;
        };

        /// Read once, then shared and never changed: Reload makes a new one.
        struct Catalog
        {
            std::string File;
            std::string Built;
            std::string Package;
            std::vector<std::string> Categories;
            /// Entries per category, for the combo.
            std::vector<int> Counts;
            std::vector<CatalogEntry> Entries;
            /// Entries without a name or a mesh.
            int Skipped = 0;
            /// Why there are no entries: no file yet, or one that can't be read.
            std::string Error;
        };

        /// A catalog entry's thumbnail on the GPU. The view holds the texture.
        struct Thumb
        {
            ID3D11ShaderResourceView* View = nullptr;
            /// Decoded, or found missing or unreadable: not tried again until the editor reopens or the catalog is read again.
            bool Tried = false;
            float Width = 0;
            float Height = 0;
        };

        // Render thread only.
        std::shared_ptr<Catalog const> TheCatalog;
        std::future<std::shared_ptr<Catalog const>> CatalogReading;
        /// By entry index of TheCatalog.
        std::vector<Thumb> Thumbs;
        int ThumbFailures = 0;
        bool WicFailureLogged = false;
        /// The Catalog tab: -1 for All, else a category index; and its rows (entry indexes) for the catalog, category and
        /// search they were made from.
        int CatalogCategory = -1;
        char CatalogFilter[128]{};
        std::vector<int> CatalogShown;
        std::string CatalogShownFilter;
        int CatalogShownCategory = -1;
        std::shared_ptr<Catalog const> CatalogShownFrom;

        std::string StringOf(json const& object, char const* key)
        {
            auto const it = object.find(key);
            return it != object.end() && it->is_string() ? it->get<std::string>() : std::string{};
        }

        double NumberOf(json const& object, char const* key)
        {
            auto const it = object.find(key);
            return it != object.end() && it->is_number() ? it->get<double>() : 0;
        }

        /// A thumbnail's full path from its path relative to the catalog folder; empty for none, or for one that is absolute or
        /// leaves the folder.
        std::wstring ThumbPath(std::filesystem::path const& folder, std::string const& relative)
        {
            if (relative.empty()) return {};
            std::filesystem::path const path(ToWide(relative));
            if (path.has_root_name() || path.has_root_directory()) return {};
            for (auto const& part : path)
            {
                if (part == L"..") return {};
            }
            return (folder / path).lexically_normal().wstring();
        }

        /// Any thread: it touches no game object and no GPU state. A missing or bad file comes back as Error, never as an
        /// exception.
        std::shared_ptr<Catalog const> ReadCatalog()
        {
            auto catalog = std::make_shared<Catalog>();
            try
            {
                auto const folder = std::filesystem::path(DataFolder()) / L"catalog";
                auto const file = folder / L"catalog.json";
                catalog->File = ToUtf8(file.wstring());
                if (!std::filesystem::exists(file))
                {
                    catalog->Error = std::format("No catalog yet ({}): build it with catalog_build in mele-mcp, then Reload.", catalog->File);
                    return catalog;
                }
                std::ifstream in(file, std::ios::binary);
                auto const doc = json::parse(in);
                auto const version = doc.at("version").get<int>();
                if (version != 1) throw BridgeError(std::format("version {}, and this bridge reads version 1", version));
                catalog->Built = StringOf(doc, "built");
                catalog->Package = StringOf(doc, "package");
                for (auto const& c : doc.at("categories")) catalog->Categories.push_back(c.get<std::string>());
                for (auto const& e : doc.at("entries"))
                {
                    CatalogEntry entry;
                    entry.Name = StringOf(e, "name");
                    entry.Mesh = StringOf(e, "mesh");
                    if (entry.Name.empty() || entry.Mesh.empty())
                    {
                        catalog->Skipped++;
                        continue;
                    }
                    entry.Source = StringOf(e, "source");
                    entry.Load = StringOf(e, "load");
                    entry.ThumbFile = ThumbPath(folder, StringOf(e, "thumb"));
                    // A category the list doesn't name joins it at the end.
                    auto category = StringOf(e, "category");
                    if (category.empty()) category = "Other";
                    auto const known = std::find(catalog->Categories.begin(), catalog->Categories.end(), category);
                    entry.Category = static_cast<int>(known - catalog->Categories.begin());
                    if (known == catalog->Categories.end()) catalog->Categories.push_back(category);
                    if (auto const size = e.find("size"); size != e.end() && size->is_array() && size->size() == 3)
                    {
                        for (size_t i = 0; i < 3; i++) entry.Size[i] = (*size)[i].is_number() ? (*size)[i].get<float>() : 0;
                    }
                    entry.Tris = static_cast<int>(NumberOf(e, "tris"));
                    entry.Placed = static_cast<int>(NumberOf(e, "placed"));
                    auto const large = e.find("large");
                    entry.Large = large != e.end() && large->is_boolean() && large->get<bool>();
                    catalog->Entries.push_back(std::move(entry));
                }
                catalog->Counts.assign(catalog->Categories.size(), 0);
                for (auto const& entry : catalog->Entries) catalog->Counts[static_cast<size_t>(entry.Category)]++;
            }
            catch (std::exception const& e)
            {
                catalog->Entries.clear();
                catalog->Counts.assign(catalog->Categories.size(), 0);
                catalog->Error = std::format("{} can't be read: {}", catalog->File.empty() ? std::string("catalog.json") : catalog->File, e.what());
            }
            return catalog;
        }

        /// The GPU copies go when the editor closes or the catalog is read again; they come back a few a frame as cells show.
        void ReleaseThumbnails()
        {
            for (auto& thumb : Thumbs)
            {
                if (thumb.View != nullptr) thumb.View->Release();
                thumb = {};
            }
            Diag.Thumbnails.store(0);
        }

        /// WIC for one frame's thumbnails. The Present thread is the game's: COM is set up for the frame only, and left as it
        /// was found. A thread already in a single-threaded apartment is fine too (WIC works in both).
        struct WicScope
        {
            bool Tried = false;
            HRESULT Com = E_FAIL;
            Microsoft::WRL::ComPtr<IWICImagingFactory> Factory;

            WicScope() = default;
            WicScope(WicScope const&) = delete;
            WicScope& operator=(WicScope const&) = delete;
            ~WicScope()
            {
                Factory.Reset();
                if (SUCCEEDED(Com)) CoUninitialize();
            }

            IWICImagingFactory* Get()
            {
                if (Tried) return Factory.Get();
                Tried = true;
                Com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
                auto hr = Com;
                if (SUCCEEDED(Com) || Com == RPC_E_CHANGED_MODE)
                    hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&Factory));
                if (FAILED(hr) && !WicFailureLogged)
                {
                    WicFailureLogged = true;
                    LEASI_ERROR("editor: no WIC imaging factory (0x{:08X}), thumbnails off", static_cast<unsigned>(hr));
                }
                return Factory.Get();
            }
        };

        /// A PNG as a texture ImGui can draw: WIC converts it to 32-bit RGBA with straight alpha, which is how ImGui blends.
        HRESULT LoadThumbnail(IWICImagingFactory* wic, std::wstring const& file, Thumb& thumb)
        {
            using Microsoft::WRL::ComPtr;
            ComPtr<IWICBitmapDecoder> decoder;
            ComPtr<IWICBitmapFrameDecode> frame;
            ComPtr<IWICFormatConverter> converter;
            UINT width = 0, height = 0;
            auto hr = wic->CreateDecoderFromFilename(file.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder);
            if (SUCCEEDED(hr)) hr = decoder->GetFrame(0, &frame);
            if (SUCCEEDED(hr)) hr = wic->CreateFormatConverter(&converter);
            if (SUCCEEDED(hr))
                hr = converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom);
            if (SUCCEEDED(hr)) hr = converter->GetSize(&width, &height);
            if (SUCCEEDED(hr) && (width == 0 || height == 0 || width > MaxThumbSide || height > MaxThumbSide)) hr = WINCODEC_ERR_IMAGESIZEOUTOFRANGE;
            if (FAILED(hr)) return hr;

            std::vector<BYTE> pixels(static_cast<size_t>(width) * height * 4);
            hr = converter->CopyPixels(nullptr, width * 4, static_cast<UINT>(pixels.size()), pixels.data());
            if (FAILED(hr)) return hr;

            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = width;
            desc.Height = height;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            desc.SampleDesc.Count = 1;
            desc.Usage = D3D11_USAGE_IMMUTABLE;
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA const data{ pixels.data(), width * 4, 0 };
            ComPtr<ID3D11Texture2D> texture;
            hr = Device->CreateTexture2D(&desc, &data, &texture);
            if (SUCCEEDED(hr)) hr = Device->CreateShaderResourceView(texture.Get(), nullptr, &thumb.View);
            if (FAILED(hr)) return hr;
            thumb.Width = static_cast<float>(width);
            thumb.Height = static_cast<float>(height);
            return S_OK;
        }

        /// An entry's thumbnail, decoded the first time its cell shows, at most ThumbsPerFrame a frame. No view: still waiting
        /// (not Tried), or none.
        Thumb const& ThumbnailOf(int index, WicScope& wic, int& budget)
        {
            auto& thumb = Thumbs[static_cast<size_t>(index)];
            if (thumb.Tried) return thumb;
            auto const& entry = TheCatalog->Entries[static_cast<size_t>(index)];
            if (entry.ThumbFile.empty())
            {
                thumb.Tried = true;
                return thumb;
            }
            if (budget <= 0) return thumb;
            budget--;
            thumb.Tried = true;
            auto const factory = wic.Get();
            auto const hr = factory != nullptr ? LoadThumbnail(factory, entry.ThumbFile, thumb) : E_NOINTERFACE;
            if (SUCCEEDED(hr)) Diag.Thumbnails.fetch_add(1);
            else if (ThumbFailures++ < 3) LEASI_WARN("editor: thumbnail {} not loaded (0x{:08X})", ToUtf8(entry.ThumbFile), static_cast<unsigned>(hr));
            return thumb;
        }

        /// The text cut to fit a width, with "..." when cut (never inside a UTF-8 sequence).
        std::string Fit(std::string const& text, float width)
        {
            if (ImGui::CalcTextSize(text.c_str()).x <= width) return text;
            auto keep = text.size();
            while (keep > 0)
            {
                keep--;
                while (keep > 0 && (static_cast<unsigned char>(text[keep]) & 0xC0) == 0x80) keep--;
                auto const cut = text.substr(0, keep) + "...";
                if (ImGui::CalcTextSize(cut.c_str()).x <= width) return cut;
            }
            return "...";
        }

        /// In the game thread's list of meshes in memory, which is sorted the way _stricmp orders.
        bool InMemory(std::vector<std::string> const& sorted, std::string const& path)
        {
            if (path.empty()) return false;
            auto const it = std::lower_bound(sorted.begin(), sorted.end(), path,
                                             [](std::string const& a, std::string const& b) { return _stricmp(a.c_str(), b.c_str()) < 0; });
            return it != sorted.end() && _stricmp(it->c_str(), path.c_str()) == 0;
        }

        Command SpawnOf(CatalogEntry const& entry)
        {
            return { .Type = Command::Kind::Spawn, .Text = entry.Mesh, .Load = entry.Load, .Source = entry.Source };
        }

        /// A Spawn for a row of the In this level list. A catalog mesh loaded earlier shows there under the catalog package's
        /// path: it spawns as its entry, so the session names the vanilla mesh and file that editor_import ports. One no entry
        /// loads goes as is, and the game thread refuses it.
        Command SpawnOfListed(std::string const& path)
        {
            if (TheCatalog != nullptr)
            {
                for (auto const& entry : TheCatalog->Entries)
                {
                    if (!entry.Load.empty() && _stricmp(entry.Load.c_str(), path.c_str()) == 0) return SpawnOf(entry);
                }
            }
            return { .Type = Command::Kind::Spawn, .Text = path };
        }

        /// One cell of the grid: the thumbnail (or a placeholder), the name under it, the details in a tooltip. A click spawns.
        void DrawCell(Snapshot const& s, int index, WicScope& wic, int& budget)
        {
            auto const& entry = TheCatalog->Entries[static_cast<size_t>(index)];
            ImGui::PushID(index);
            auto const clicked = ImGui::InvisibleButton("##prop", { CellWidth, ThumbSize + 4 + ImGui::GetTextLineHeight() });
            ImGui::PopID();
            auto const hovered = ImGui::IsItemHovered();
            auto const held = ImGui::IsItemActive();
            auto const min = ImGui::GetItemRectMin();
            auto const draw = ImGui::GetWindowDrawList();

            ImVec2 const box{ min.x + (CellWidth - ThumbSize) * 0.5f, min.y };
            ImVec2 const boxEnd{ box.x + ThumbSize, box.y + ThumbSize };
            draw->AddRectFilled(box, boxEnd, ImGui::GetColorU32(held ? ImGuiCol_ButtonActive : hovered ? ImGuiCol_ButtonHovered : ImGuiCol_FrameBg), 4);
            auto const& thumb = ThumbnailOf(index, wic, budget);
            if (thumb.View != nullptr)
            {
                // Fitted to the square, its shape kept.
                auto const scale = ThumbSize / std::max(thumb.Width, thumb.Height);
                auto const w = thumb.Width * scale, h = thumb.Height * scale;
                ImVec2 const at{ box.x + (ThumbSize - w) * 0.5f, box.y + (ThumbSize - h) * 0.5f };
                draw->AddImage(static_cast<ImTextureID>(reinterpret_cast<uintptr_t>(thumb.View)), at, { at.x + w, at.y + h });
            }
            else if (thumb.Tried)
            {
                auto const size = ImGui::CalcTextSize("no image");
                draw->AddText({ box.x + (ThumbSize - size.x) * 0.5f, box.y + (ThumbSize - size.y) * 0.5f }, ImGui::GetColorU32(ImGuiCol_TextDisabled), "no image");
            }
            auto const label = Fit(entry.Name, CellWidth);
            draw->AddText({ min.x + (CellWidth - ImGui::CalcTextSize(label.c_str()).x) * 0.5f, boxEnd.y + 4 }, ImGui::GetColorU32(ImGuiCol_Text), label.c_str());

            if (ImGui::BeginItemTooltip())
            {
                ImGui::TextUnformatted(entry.Name.c_str());
                ImGui::TextDisabled("%s", TheCatalog->Categories[static_cast<size_t>(entry.Category)].c_str());
                ImGui::Text("Size %.0f x %.0f x %.0f%s   %d triangles", entry.Size[0], entry.Size[1], entry.Size[2], entry.Large ? " (large)" : "", entry.Tris);
                if (entry.Placed > 0) ImGui::Text("Placed %d times in the game's levels", entry.Placed);
                ImGui::Text("From %s", entry.Source.empty() ? "-" : entry.Source.c_str());
                ImGui::TextDisabled("%s", entry.Mesh.c_str());
                // As of the In this level list's last read.
                if (s.Meshes != nullptr)
                {
                    if (InMemory(*s.Meshes, entry.Mesh)) ImGui::TextDisabled("In memory: a level here has it");
                    else if (InMemory(*s.Meshes, entry.Load)) ImGui::TextDisabled("In memory: loaded from the catalog");
                    else if (!entry.Load.empty()) ImGui::TextDisabled("Loaded from the catalog package on click");
                    else ImGui::TextDisabled("Not in memory, and not in the catalog package");
                }
                ImGui::EndTooltip();
            }
            if (clicked) Post(SpawnOf(entry));
        }

        /// The shown entries in rows of cells; only the rows on screen are drawn (and their thumbnails decoded).
        void DrawCatalogGrid(Snapshot const& s)
        {
            auto const& style = ImGui::GetStyle();
            auto const columns = std::max(1, static_cast<int>((ImGui::GetContentRegionAvail().x + style.ItemSpacing.x) / (CellWidth + style.ItemSpacing.x)));
            auto const count = static_cast<int>(CatalogShown.size());
            auto const rows = (count + columns - 1) / columns;
            WicScope wic;
            auto budget = ThumbsPerFrame;
            ImGuiListClipper clipper;
            clipper.Begin(rows, ThumbSize + 4 + ImGui::GetTextLineHeight() + style.ItemSpacing.y);
            while (clipper.Step())
            {
                for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                {
                    for (int column = 0; column < columns; column++)
                    {
                        auto const at = row * columns + column;
                        if (at >= count) break;
                        if (column > 0) ImGui::SameLine();
                        DrawCell(s, CatalogShown[static_cast<size_t>(at)], wic, budget);
                    }
                }
            }
        }

        /// The Catalog tab: a category, a search, and the grid. The file is read on first sight and on Reload, in the
        /// background (a few hundred kilobytes of JSON).
        void DrawCatalog(Snapshot const& s)
        {
            if (TheCatalog == nullptr && !CatalogReading.valid()) CatalogReading = std::async(std::launch::async, ReadCatalog);
            if (CatalogReading.valid() && CatalogReading.wait_for(std::chrono::seconds(0)) == std::future_status::ready)
            {
                // Swapped before the grid draws: no cell of this frame holds an old thumbnail.
                ReleaseThumbnails();
                TheCatalog = CatalogReading.get();
                Thumbs.assign(TheCatalog->Entries.size(), Thumb{});
                ThumbFailures = 0;
                if (CatalogCategory >= static_cast<int>(TheCatalog->Categories.size())) CatalogCategory = -1;
                Diag.CatalogEntries.store(TheCatalog->Error.empty() ? static_cast<int>(TheCatalog->Entries.size()) : -1);
                if (TheCatalog->Error.empty())
                    LEASI_INFO("editor: catalog {} read: {} props ({} skipped), package {}, built {}", TheCatalog->File, TheCatalog->Entries.size(),
                               TheCatalog->Skipped, TheCatalog->Package, TheCatalog->Built);
                else
                    LEASI_WARN("editor: {}", TheCatalog->Error);
            }
            auto const catalog = TheCatalog;

            auto const all = catalog != nullptr ? static_cast<int>(catalog->Entries.size()) : 0;
            auto const preview = catalog == nullptr || CatalogCategory < 0 ? std::string("All") : catalog->Categories[static_cast<size_t>(CatalogCategory)];
            ImGui::SetNextItemWidth(150);
            if (ImGui::BeginCombo("##category", preview.c_str()))
            {
                if (ImGui::Selectable(std::format("All ({})", all).c_str(), CatalogCategory < 0)) CatalogCategory = -1;
                for (size_t i = 0; catalog != nullptr && i < catalog->Categories.size(); i++)
                {
                    ImGui::PushID(static_cast<int>(i));
                    if (ImGui::Selectable(std::format("{} ({})", catalog->Categories[i], catalog->Counts[i]).c_str(), CatalogCategory == static_cast<int>(i)))
                        CatalogCategory = static_cast<int>(i);
                    ImGui::PopID();
                }
                ImGui::EndCombo();
            }
            ImGui::SameLine();
            ImGui::SetNextItemWidth(-80);
            ImGui::InputTextWithHint("##catalogFilter", "Search, e.g. crate", CatalogFilter, sizeof CatalogFilter);
            ImGui::SameLine();
            ImGui::BeginDisabled(CatalogReading.valid());
            if (ImGui::Button("Reload")) CatalogReading = std::async(std::launch::async, ReadCatalog);
            ImGui::EndDisabled();
            if (ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled)) ImGui::SetTooltip("Reads catalog.json again, after catalog_build in mele-mcp.");

            if (catalog == nullptr)
            {
                ImGui::TextDisabled("Reading the catalog...");
                return;
            }
            if (!catalog->Error.empty())
            {
                ImGui::PushStyleColor(ImGuiCol_Text, ImVec4(1, 0.6f, 0.3f, 1));
                ImGui::TextWrapped("%s", catalog->Error.c_str());
                ImGui::PopStyleColor();
                ImGui::TextDisabled("The In this level tab still lists the meshes in memory.");
                return;
            }

            if (CatalogShownFrom != catalog || CatalogShownFilter != CatalogFilter || CatalogShownCategory != CatalogCategory)
            {
                CatalogShownFrom = catalog;
                CatalogShownFilter = CatalogFilter;
                CatalogShownCategory = CatalogCategory;
                CatalogShown.clear();
                for (int i = 0; i < all; i++)
                {
                    auto const& entry = catalog->Entries[static_cast<size_t>(i)];
                    if (CatalogCategory >= 0 && entry.Category != CatalogCategory) continue;
                    if (!CatalogShownFilter.empty() && !ContainsNoCase(entry.Name, CatalogShownFilter) && !ContainsNoCase(entry.Mesh, CatalogShownFilter)) continue;
                    CatalogShown.push_back(i);
                }
            }
            ImGui::TextDisabled("%d of %d. Click one: it appears where the view points.", static_cast<int>(CatalogShown.size()), all);
            if (ImGui::BeginChild("##grid")) DrawCatalogGrid(s);
            ImGui::EndChild();
        }

        /// The In this level tab (Phase 1's list): the meshes in memory, searchable; a click spawns one where the view points.
        void DrawLevelMeshes(Snapshot const& s)
        {
            ImGui::TextDisabled("Meshes in memory. Click one: it appears where the view points.");
            ImGui::SetNextItemWidth(-90);
            ImGui::InputTextWithHint("##filter", "Search, e.g. crate", Filter, sizeof Filter);
            ImGui::SameLine();
            if (ImGui::Button("Refresh")) Post({ .Type = Command::Kind::RefreshMeshes });

            if (s.Meshes == nullptr)
            {
                ImGui::TextDisabled("Reading the meshes in memory...");
                return;
            }
            if (ShownFrom != s.Meshes || ShownFilter != Filter)
            {
                ShownFrom = s.Meshes;
                ShownFilter = Filter;
                Shown.clear();
                for (int i = 0; i < static_cast<int>(s.Meshes->size()); i++)
                {
                    if (ShownFilter.empty() || ContainsNoCase((*s.Meshes)[i], ShownFilter)) Shown.push_back(i);
                }
            }
            ImGui::TextDisabled("%d of %d", static_cast<int>(Shown.size()), static_cast<int>(s.Meshes->size()));
            if (ImGui::BeginChild("##meshes"))
            {
                ImGuiListClipper clipper;
                clipper.Begin(static_cast<int>(Shown.size()));
                while (clipper.Step())
                {
                    for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                    {
                        if (Shown[row] >= static_cast<int>(s.Meshes->size())) continue;
                        auto const& path = (*s.Meshes)[Shown[row]];
                        ImGui::PushID(row);
                        if (ImGui::Selectable(path.c_str())) Post(SpawnOfListed(path));
                        ImGui::PopID();
                    }
                }
            }
            ImGui::EndChild();
        }

        ImU32 ColourOf(LightBlock const& light, int alpha = 255) { return IM_COL32(light.Color[0], light.Color[1], light.Color[2], alpha); }

        /// An icon's shape tells its source (docs/07 §12): a disc for ours, a diamond for an NPC's light, a square for a level's, a
        /// star for an effect.
        enum class Shape { Disc, Diamond, Square, Star };

        Shape ShapeOf(LightBlock const& light)
        {
            return light.Kind == LightKind::Npc ? Shape::Diamond : light.Kind == LightKind::Vanilla ? Shape::Square : Shape::Disc;
        }

        /// A shape about a centre, about as far out as a disc of that radius, filled or as an outline. The star's points reach a
        /// little further: its arms are thin.
        void DrawShape(ImDrawList* draw, ImVec2 centre, float radius, Shape shape, ImU32 colour, bool filled, float thickness = 2)
        {
            if (shape == Shape::Disc)
            {
                if (filled) draw->AddCircleFilled(centre, radius, colour);
                else draw->AddCircle(centre, radius, colour, 0, thickness);
                return;
            }
            ImVec2 points[10];
            auto count = 4;
            if (shape == Shape::Diamond)
            {
                points[0] = { centre.x, centre.y - radius };
                points[1] = { centre.x + radius, centre.y };
                points[2] = { centre.x, centre.y + radius };
                points[3] = { centre.x - radius, centre.y };
            }
            else if (shape == Shape::Square)
            {
                auto const half = radius * 0.85f;
                points[0] = { centre.x - half, centre.y - half };
                points[1] = { centre.x + half, centre.y - half };
                points[2] = { centre.x + half, centre.y + half };
                points[3] = { centre.x - half, centre.y + half };
            }
            else
            {
                count = 10;
                for (int i = 0; i < count; i++)
                {
                    auto const angle = -1.5707963f + static_cast<float>(i) * 3.14159265f / 5;
                    auto const reach = i % 2 == 0 ? radius * 1.2f : radius * 0.5f;
                    points[i] = { centre.x + std::cos(angle) * reach, centre.y + std::sin(angle) * reach };
                }
            }
            if (!filled) draw->AddPolyline(points, count, colour, ImDrawFlags_Closed, thickness);
            else if (shape == Shape::Star) draw->AddConcavePolyFilled(points, count, colour);
            else draw->AddConvexPolyFilled(points, count, colour);
        }

        /// The icons' legend (docs/07 §12), the shapes drawn as the world has them. The type letters are the level lights' (Phase
        /// 5b).
        void DrawLegend()
        {
            auto const draw = ImGui::GetWindowDrawList();
            auto const size = ImGui::GetTextLineHeight();
            auto const item = [&](Shape shape, char const* text) {
                auto const at = ImGui::GetCursorScreenPos();
                ImGui::Dummy({ size, size });
                DrawShape(draw, { at.x + size * 0.5f, at.y + size * 0.5f }, size * 0.32f, shape, ImGui::GetColorU32(ImGuiCol_TextDisabled), false, 1.5f);
                ImGui::SameLine(0, 2);
                ImGui::TextDisabled("%s", text);
                ImGui::SameLine();
            };
            item(Shape::Disc, "ours");
            item(Shape::Diamond, "NPC");
            item(Shape::Square, "level light");
            item(Shape::Star, "effect");
            ImGui::TextDisabled("\u00b7 B baked, C characters only, U special channel, D dynamic");
        }

        /// A row's marks: a light or an effect with an edit, saved or not.
        std::string EditMark(bool edited, bool unsaved) { return unsaved ? "   (unsaved)" : edited ? "   (edited)" : ""; }

        bool SameNoCase(std::string const& a, std::string const& b) { return _stricmp(a.c_str(), b.c_str()) == 0; }

        /// One NPC's section of the Lights tab (docs/07 §12): "Dorn (claude_npc_02)" with Add light, then its lights, each a row
        /// that selects it. A light whose NPC's level is not visible has its NPC's section saying so, and one whose NPC levels.json
        /// no longer lists has a section under its NPC's tag; neither offers Add light.
        void DrawNpcSection(Snapshot const& s, NpcSection const& npc)
        {
            ImGui::PushID(npc.Tag.c_str());
            ImGui::Separator();
            if (npc.Away)
            {
                ImGui::Text("%s (%s)", npc.Name.c_str(), npc.Tag.c_str());
                ImGui::SameLine();
                ImGui::TextDisabled("(%s is not visible here: its lights wait for it)", npc.Level.c_str());
            }
            else if (npc.Name.empty())
            {
                ImGui::TextUnformatted(npc.Tag.c_str());
                ImGui::SameLine();
                ImGui::TextDisabled("(levels.json no longer lists this NPC: run mod_build)");
            }
            else
            {
                ImGui::Text("%s (%s)", npc.Name.c_str(), npc.Tag.c_str());
                ImGui::SameLine();
                if (ImGui::SmallButton("Add light")) Post({ .Type = Command::Kind::AddNpcLight, .Text = npc.Tag });
                if (ImGui::IsItemHovered())
                    ImGui::SetTooltip("A face light at [60, 0, 80] from %s's centre, radius 100, brightness 1.2: it goes where %s goes.", npc.Name.c_str(),
                                      npc.Name.c_str());
            }
            auto any = false;
            for (size_t i = 0; i < s.Lights.size(); i++)
            {
                auto const& l = s.Lights[i];
                if (l.Light.Kind != LightKind::Npc || !SameNoCase(l.Npc, npc.Tag)) continue;
                any = true;
                ImGui::PushID(static_cast<int>(i));
                ImGui::ColorButton("##colour", ImGui::ColorConvertU32ToFloat4(ColourOf(l.Light)), ImGuiColorEditFlags_NoTooltip | ImGuiColorEditFlags_NoPicker,
                                   { ImGui::GetTextLineHeight(), ImGui::GetTextLineHeight() });
                ImGui::SameLine();
                auto const row = std::format("{} \u00b7 NPC \u00b7 radius {:.0f} \u00b7 brightness {:.3g}{}{}", l.Tag, l.Light.Radius, l.Light.Brightness,
                                             l.Light.Enabled ? "" : "   (off)", EditMark(l.Edited, l.Unsaved));
                if (ImGui::Selectable(row.c_str(), s.Selected && s.SelectedPath == l.Path)) Post({ .Type = Command::Kind::PickLight, .Text = l.Path });
                ImGui::PopID();
            }
            if (!any) ImGui::TextDisabled("no lights");
            ImGui::PopID();
        }

        /// The Lights tab (docs/07 §11, §12): Prop light and Room light add one over where the view points, NPC light one for the
        /// selected NPC of ours; the list (the editor's lights and the YAMLs' lights: entries, deleted ones left out) selects one,
        /// then a section per NPC of ours (DrawNpcSection). While it is open, a click near an icon does too. The levels' lights
        /// have icons of their own shapes (the legend), not rows here.
        void DrawLights(Snapshot const& s)
        {
            if (ImGui::Button("Prop light")) Post({ .Type = Command::Kind::AddLight, .On = false });
            if (ImGui::IsItemHovered())
                ImGui::SetTooltip("A point light on Gameplay_4 only: it lights our props, never walls, pawns or vanilla meshes. Brightness and "
                                  "colour from the last prop light you tuned on this map.");
            ImGui::SameLine();
            if (ImGui::Button("Room light")) Post({ .Type = Command::Kind::AddLight, .On = true });
            if (ImGui::IsItemHovered()) ImGui::SetTooltip("Lights walls, vanilla meshes, pawns and props alike.");
            ImGui::SameLine();
            // The game thread says why when the selected pawn is not one of ours.
            ImGui::BeginDisabled(!s.Selected || !s.SelectedPawn);
            if (ImGui::Button("NPC light")) Post({ .Type = Command::Kind::AddNpcLight });
            ImGui::EndDisabled();
            if (ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled))
                ImGui::SetTooltip("%s", s.Selected && s.SelectedPawn ? "A face light for the selected NPC of ours (npcs: in a level YAML): it goes where the NPC goes."
                                                                     : "Select one of our NPCs first, or use Add light on its section below.");
            auto const ours = [](LightIcon const& l) { return l.Light.Kind == LightKind::Props || l.Light.Kind == LightKind::Room; };
            ImGui::TextDisabled("%d lights. Click one here, or near its icon in the world, to select it.",
                                static_cast<int>(std::count_if(s.Lights.begin(), s.Lights.end(), ours)));
            DrawLegend();
            if (ImGui::BeginChild("##lights"))
            {
                for (size_t i = 0; i < s.Lights.size(); i++)
                {
                    auto const& l = s.Lights[i];
                    if (!ours(l)) continue;
                    ImGui::PushID(static_cast<int>(i));
                    ImGui::ColorButton("##colour", ImGui::ColorConvertU32ToFloat4(ColourOf(l.Light)), ImGuiColorEditFlags_NoTooltip | ImGuiColorEditFlags_NoPicker,
                                       { ImGui::GetTextLineHeight(), ImGui::GetTextLineHeight() });
                    ImGui::SameLine();
                    auto const row = std::format("{}   {}   radius {:.0f}   brightness {:.3g}{}{}", l.Tag, l.Light.Kind == LightKind::Room ? "room" : "props",
                                                 l.Light.Radius, l.Light.Brightness, l.Light.Enabled ? "" : "   (off)", EditMark(l.Edited, l.Unsaved));
                    if (ImGui::Selectable(row.c_str(), s.Selected && s.SelectedPath == l.Path)) Post({ .Type = Command::Kind::PickLight, .Text = l.Path });
                    ImGui::PopID();
                }
                for (auto const& npc : s.Npcs) DrawNpcSection(s, npc);
            }
            ImGui::EndChild();
        }

        /// The Effects tab (docs/07 §12): the emitters of the visible levels, nearest first. One the editor leaves alone is greyed
        /// with its reason. A click on a row selects it; while the tab is open, a click near its icon does too.
        void DrawEffects(Snapshot const& s)
        {
            ImGui::TextDisabled("%d effects, nearest first. Click one here, or near its icon in the world, to select it.", static_cast<int>(s.Effects.size()));
            if (ImGui::BeginChild("##effects"))
            {
                ImGuiListClipper clipper;
                clipper.Begin(static_cast<int>(s.Effects.size()));
                while (clipper.Step())
                {
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        auto const& e = s.Effects[static_cast<size_t>(i)];
                        auto row = std::format("{} \u00b7 {} \u00b7 {} \u00b7 {:.1f} m", e.Name, e.Template, e.Level, e.Metres);
                        if (e.Deleted) row += "   (deleted)";
                        else if (e.Hidden) row += e.Forked ? "   (hidden: removed in the YAML)" : "   (hidden)";
                        row += EditMark(e.Edited, e.Unsaved);
                        if (!e.Refusal.empty()) row += "   not edited: " + e.Refusal;
                        ImGui::PushID(i);
                        if (!e.Refusal.empty()) ImGui::PushStyleColor(ImGuiCol_Text, ImGui::GetStyleColorVec4(ImGuiCol_TextDisabled));
                        if (ImGui::Selectable(row.c_str(), s.Selected && s.SelectedPath == e.Path)) Post({ .Type = Command::Kind::PickEffect, .Text = e.Path });
                        if (!e.Refusal.empty())
                        {
                            ImGui::PopStyleColor();
                            if (ImGui::IsItemHovered()) ImGui::SetTooltip("not edited: %s", e.Refusal.c_str());
                        }
                        ImGui::PopID();
                    }
                }
            }
            ImGui::EndChild();
        }

        /// Props: the catalog as a grid of thumbnails, and the meshes in memory as a list. Lights: the editor's lights. Effects: the
        /// levels' emitters.
        void DrawProps(Snapshot const& s, float height)
        {
            LightsTabOpen = false;
            EffectsTabOpen = false;
            if (!ShowProps) return;
            // Asked once per list: a map change drops the list, and the new map's is asked for.
            if (s.Meshes != nullptr) MeshesAsked = false;
            else ShownFrom.reset();
            if (s.Meshes == nullptr && !MeshesAsked)
            {
                Post({ .Type = Command::Kind::RefreshMeshes });
                MeshesAsked = true;
            }
            ImGui::SetNextWindowPos({ 10, 70 }, ImGuiCond_FirstUseEver);
            ImGui::SetNextWindowSize({ 540, height * 0.6f }, ImGuiCond_FirstUseEver);
            ImGui::SetNextWindowBgAlpha(0.92f);
            if (ImGui::Begin("Props", &ShowProps, ImGuiWindowFlags_NoSavedSettings))
            {
                if (ImGui::BeginTabBar("##props"))
                {
                    if (ImGui::BeginTabItem("Catalog"))
                    {
                        DrawCatalog(s);
                        ImGui::EndTabItem();
                    }
                    if (ImGui::BeginTabItem("In this level"))
                    {
                        DrawLevelMeshes(s);
                        ImGui::EndTabItem();
                    }
                    if (ImGui::BeginTabItem("Lights"))
                    {
                        LightsTabOpen = true;
                        DrawLights(s);
                        ImGui::EndTabItem();
                    }
                    if (ImGui::BeginTabItem("Effects"))
                    {
                        EffectsTabOpen = true;
                        DrawEffects(s);
                        ImGui::EndTabItem();
                    }
                    ImGui::EndTabBar();
                }
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
                if (Diag.CatalogEntries.load() >= 0) ImGui::Text("Catalog: %d props   %d thumbnails on the GPU", Diag.CatalogEntries.load(), Diag.Thumbnails.load());
                else ImGui::Text("Catalog: not read, or unreadable (see the Props window's Catalog tab)");
                if (s.Session != nullptr)
                {
                    // What the re-apply passes did with the map's session file (docs/07 §10).
                    auto const& session = *s.Session;
                    ImGui::Text("Session: %s", session.File.empty() ? "-" : session.File.c_str());
                    if (!session.Error.empty()) ImGui::TextColored(ImVec4(1, 0.6f, 0.3f, 1), "  %s", session.Error.c_str());
                    ImGui::Text("  %d applied   %d dropped   %d waiting   last pass at frame %llu", session.Applied, session.Dropped,
                                static_cast<int>(session.Pending.size()), session.PassFrame);
                    constexpr size_t ShownPending = 6;
                    for (size_t i = 0; i < session.Pending.size() && i < ShownPending; i++) ImGui::TextDisabled("  %s", session.Pending[i].c_str());
                    if (session.Pending.size() > ShownPending) ImGui::TextDisabled("  and %d more (the editor state lists them all)", static_cast<int>(session.Pending.size() - ShownPending));
                    if (!session.Levels.empty())
                    {
                        std::string levels;
                        for (auto const& level : session.Levels) levels += (levels.empty() ? "" : ", ") + level;
                        // Wrapped at a fixed width: the window sizes itself to its content.
                        ImGui::PushTextWrapPos(ImGui::GetCursorPosX() + ImGui::GetFontSize() * 40);
                        ImGui::TextUnformatted(("  Levels: " + levels).c_str());
                        ImGui::PopTextWrapPos();
                    }
                }
                if (s.OurLevels != nullptr)
                {
                    // Which levels are ours (docs/07 §11): read at each map change.
                    auto const& levels = *s.OurLevels;
                    if (!levels.Problem.empty())
                    {
                        ImGui::TextColored(ImVec4(1, 0.6f, 0.3f, 1), "%s", levels.Problem.c_str());
                    }
                    else
                    {
                        std::string names;
                        for (auto const& level : levels.Levels) names += (names.empty() ? "" : ", ") + level;
                        if (names.empty()) names = "no levels";
                        ImGui::PushTextWrapPos(ImGui::GetCursorPosX() + ImGui::GetFontSize() * 40);
                        ImGui::TextUnformatted(std::format("levels.json: {} ({})", names, levels.Dlc.empty() ? "no dlc folder" : levels.Dlc).c_str());
                        ImGui::PopTextWrapPos();
                    }
                }
                ImGui::Checkbox("ImGui demo window", &ShowDemo);
            }
            ImGui::End();
            if (ShowDemo) ImGui::ShowDemoWindow(&ShowDemo);
        }

        /// A light's radius as a circle about it in each world plane, through the gizmo's projection.
        void DrawRings(LightIcon const& l, float const* view, float const* projection, float width, float height)
        {
            constexpr int Segments = 64;
            auto const draw = ImGui::GetBackgroundDrawList();
            auto const colour = ColourOf(l.Light, 200);
            for (int plane = 0; plane < 3; plane++)
            {
                ImVec2 last{};
                auto hadLast = false;
                for (int i = 0; i <= Segments; i++)
                {
                    auto const angle = 6.2831853f * static_cast<float>(i) / Segments;
                    auto const a = std::cos(angle) * l.Light.Radius, b = std::sin(angle) * l.Light.Radius;
                    auto point = l.Location;
                    if (plane == 0) point = { point.X + a, point.Y + b, point.Z };
                    else if (plane == 1) point = { point.X + a, point.Y, point.Z + b };
                    else point = { point.X, point.Y + a, point.Z + b };
                    float x = 0, y = 0;
                    auto const shown = ProjectPoint(view, projection, point, width, height, x, y);
                    if (shown && hadLast) draw->AddLine(last, { x, y }, colour, 1.5f);
                    hadLast = shown;
                    last = { x, y };
                }
            }
        }

        /// A listed light's icon, in its colour and its source's shape, hollow when off, a room light with a second ring.
        void DrawLightIcon(ImDrawList* draw, LightIcon const& l, float const* view, float const* projection, float width, float height)
        {
            float x = 0, y = 0;
            if (!ProjectPoint(view, projection, l.Location, width, height, x, y)) return;
            auto const shape = ShapeOf(l.Light);
            // A dark rim: a white light's icon on a bright wall.
            DrawShape(draw, { x, y }, IconRadius + 1.5f, shape, IM_COL32(0, 0, 0, 200), false, 3);
            if (l.Light.Enabled) DrawShape(draw, { x, y }, IconRadius, shape, ColourOf(l.Light), true);
            else DrawShape(draw, { x, y }, IconRadius - 1, shape, ColourOf(l.Light), false, 2);
            if (l.Light.Kind == LightKind::Room) draw->AddCircle({ x, y }, IconRadius + 4, ColourOf(l.Light, 200), 0, 1.5f);
        }

        /// While one of an NPC's lights is selected, its NPC's reference pose (docs/07 §12), where the light's offset is measured
        /// from: a cross on the floor plane through the pawn's centre, and a tick along its yaw (forward).
        void DrawNpcMarker(Snapshot const& s, float const* view, float const* projection, float width, float height)
        {
            if (!s.Selected || !s.SelectedLight || !s.SelectedHasPose) return;
            constexpr float Arm = 16, Tick = 48;
            auto const draw = ImGui::GetBackgroundDrawList();
            auto const& at = s.SelectedPoseLocation;
            auto const yaw = static_cast<float>(s.SelectedPoseYaw) * UnitsToRadians;
            auto const line = [&](FVector a, FVector b, ImU32 colour, float thickness) {
                float ax = 0, ay = 0, bx = 0, by = 0;
                if (!ProjectPoint(view, projection, a, width, height, ax, ay) || !ProjectPoint(view, projection, b, width, height, bx, by)) return;
                draw->AddLine({ ax, ay }, { bx, by }, IM_COL32(0, 0, 0, 200), thickness + 2);
                draw->AddLine({ ax, ay }, { bx, by }, colour, thickness);
            };
            auto const colour = IM_COL32(255, 200, 60, 255);
            line({ at.X - Arm, at.Y, at.Z }, { at.X + Arm, at.Y, at.Z }, colour, 2);
            line({ at.X, at.Y - Arm, at.Z }, { at.X, at.Y + Arm, at.Z }, colour, 2);
            line(at, { at.X + std::cos(yaw) * Tick, at.Y + std::sin(yaw) * Tick, at.Z }, colour, 3);
        }

        /// The effects that have an icon (docs/07 §12): each one with an edit, always; with the Effects tab open or Alt held, the
        /// others within 30 m of the camera too, at most 50. The snapshot lists them nearest first.
        std::vector<EffectIcon const*> ShownEffects(Snapshot const& s)
        {
            auto const all = EffectsTabOpen || ImGui::GetIO().KeyAlt;
            std::vector<EffectIcon const*> shown;
            size_t untouched = 0;
            for (auto const& e : s.Effects)
            {
                if (e.Edited)
                {
                    shown.push_back(&e);
                }
                else if (all && e.Metres * 100 <= EffectIconReach && untouched < MaxEffectIcons)
                {
                    shown.push_back(&e);
                    untouched++;
                }
            }
            return shown;
        }

        /// An effect's icon: a star, amber, grey when the editor leaves it alone, hollow while it is hidden (deleted, or hidden by
        /// the build or the game).
        void DrawEffectIcon(ImDrawList* draw, EffectIcon const& e, float const* view, float const* projection, float width, float height)
        {
            float x = 0, y = 0;
            if (!ProjectPoint(view, projection, e.Location, width, height, x, y)) return;
            auto const colour = e.Refusal.empty() ? IM_COL32(255, 190, 70, 255) : IM_COL32(150, 150, 150, 255);
            DrawShape(draw, { x, y }, IconRadius + 1.5f, Shape::Star, IM_COL32(0, 0, 0, 200), false, 3);
            if (e.Deleted || e.Hidden) DrawShape(draw, { x, y }, IconRadius - 1, Shape::Star, colour, false, 2);
            else DrawShape(draw, { x, y }, IconRadius, Shape::Star, colour, true);
        }

        float CameraDistance(Snapshot const& s, FVector location)
        {
            return std::hypot(location.X - s.Pov.Location.X, location.Y - s.Pov.Location.Y, location.Z - s.Pov.Location.Z);
        }

        /// Every listed light's icon and every shown effect's star, far to near: where two overlap, the one on top is the nearer,
        /// the one IconAt picks. The selected light's radius under them all.
        void DrawIcons(Snapshot const& s, float const* view, float const* projection, float width, float height)
        {
            for (auto const& l : s.Lights)
            {
                if (s.Selected && s.SelectedPath == l.Path) DrawRings(l, view, projection, width, height);
            }
            struct Icon
            {
                float Distance = 0;
                LightIcon const* Light = nullptr;
                EffectIcon const* Effect = nullptr;
            };
            std::vector<Icon> icons;
            for (auto const& l : s.Lights) icons.push_back({ CameraDistance(s, l.Location), &l, nullptr });
            for (auto const e : ShownEffects(s)) icons.push_back({ CameraDistance(s, e->Location), nullptr, e });
            std::stable_sort(icons.begin(), icons.end(), [](Icon const& a, Icon const& b) { return a.Distance > b.Distance; });
            auto const draw = ImGui::GetBackgroundDrawList();
            for (auto const& icon : icons)
            {
                if (icon.Light != nullptr) DrawLightIcon(draw, *icon.Light, view, projection, width, height);
                else DrawEffectIcon(draw, *icon.Effect, view, projection, width, height);
            }
        }

        /// What a click picks among the icons within 12 px of it (docs/07 §12): one whose drawn shape is under the cursor beats one
        /// only near it, then the one nearest the camera, so a light in the next room never wins over the one in front, and a
        /// click on the icon drawn on top (DrawIcons: far to near) picks it. Lights and effects as asked; none: an empty path.
        struct IconPick
        {
            std::string Path;
            bool Effect = false;
        };

        IconPick IconAt(Snapshot const& s, float const* view, float const* projection, float width, float height, ImVec2 at, bool lights, bool effects)
        {
            IconPick pick;
            auto best = 0.f;
            auto covered = false;
            auto const consider = [&](FVector location, std::string const& path, bool effect) {
                float x = 0, y = 0;
                if (!ProjectPoint(view, projection, location, width, height, x, y)) return;
                auto const pixels = std::hypot(x - at.x, y - at.y);
                if (pixels > IconPickPixels) return;
                auto const under = pixels <= IconRadius + 1.5f;
                auto const distance = CameraDistance(s, location);
                if (pick.Path.empty() || (under && !covered) || (under == covered && distance < best))
                {
                    best = distance;
                    covered = under;
                    pick = { path, effect };
                }
            };
            if (lights)
            {
                for (auto const& l : s.Lights) consider(l.Location, l.Path, false);
            }
            if (effects)
            {
                for (auto const e : ShownEffects(s)) consider(e->Location, e->Path, true);
            }
            return pick;
        }

        /// DrawScale3D after a scale drag: the dragged axis (all three for the centre handle) is its row's length over DrawScale,
        /// the others stay as they were. Ctrl rounds to 0.1, else to 4 decimals; never below the step.
        FVector ScaleOf(float const* m, bool snap)
        {
            float scale[3] = { DragScale3D.X, DragScale3D.Y, DragScale3D.Z };
            for (int i = 0; i < 3; i++)
            {
                auto const axis = static_cast<ImGuizmo::MOVETYPE>(ImGuizmo::MT_SCALE_X + i);
                if (DragHandle != axis && DragHandle != ImGuizmo::MT_SCALE_XYZ) continue;
                auto const* r = m + i * 4;
                auto const value = std::sqrt(r[0] * r[0] + r[1] * r[1] + r[2] * r[2]) / DragDrawScale;
                auto const step = snap ? 0.1f : 0.0001f;
                scale[i] = std::max(std::round(value / step) * step, step);
            }
            return { scale[0], scale[1], scale[2] };
        }

        void DrawGizmo(Snapshot const& s, float const* view, float const* projection, float width, float height)
        {
            if (!s.Selected)
            {
                ResetGizmo();
                // Selected again, the actor's refusal is said again.
                RefusedFor.clear();
                return;
            }
            // A light only moves (docs/07 §11): the move gizmo, whatever tool T, R or G chose for props.
            auto const tool = s.SelectedLight ? Tool::Move : Current;
            if (Dragging && DraggedPath != s.SelectedPath) ResetGizmo();
            if (tool != LastTool)
            {
                ResetGizmo();
                LastTool = tool;
            }

            // No gizmo on a selection the editor won't change (a vanilla door), nor for R and G on one they can't take (a pawn's
            // scale, a mirrored actor): said once.
            auto why = ActorRefusal(s, tool);
            if (why.empty()) why = ToolRefusal(s, tool);
            if (!why.empty())
            {
                ResetGizmo();
                auto const key = std::format("{}\n{}", s.SelectedPath, static_cast<int>(tool));
                if (RefusedFor != key)
                {
                    RefusedFor = key;
                    Refuse(why);
                }
            }
            else
            {
                RefusedFor.clear();
                float model[16];
                ModelMatrix(s.SelectedLocation, s.SelectedRotation, s.SelectedDrawScale, s.SelectedDrawScale3D, model);
                // While dragging, ImGuizmo's own last matrix leads (the game applies each command a frame or two later), exactly as
                // it returned it: a turn adds each frame's rotation to it.
                if (Dragging) std::memcpy(model, DragModel, sizeof model);
                ImGuizmo::SetOrthographic(false);
                ImGuizmo::SetDrawlist(ImGui::GetBackgroundDrawList());
                ImGuizmo::SetRect(0, 0, width, height);
                // A pawn turns about the vertical only (the level YAML keeps a yaw for npcs); anything else on three local rings.
                auto const pawnTurn = tool == Tool::Turn && s.SelectedPawn;
                auto const operation = tool == Tool::Move ? ImGuizmo::TRANSLATE
                                       : tool == Tool::Scale ? ImGuizmo::SCALE
                                       : pawnTurn ? ImGuizmo::ROTATE_Z
                                                  : ImGuizmo::ROTATE_X | ImGuizmo::ROTATE_Y | ImGuizmo::ROTATE_Z;
                auto const mode = tool == Tool::Move || pawnTurn ? ImGuizmo::WORLD : ImGuizmo::LOCAL;
                // Ctrl snaps the move to 10 units and the turn to 15 degrees, from where the drag started. A scale is rounded below
                // instead: ImGuizmo's snap would round the ratio to the scale at the start.
                auto const ctrl = ImGui::GetIO().KeyCtrl;
                float const snap[3] = { tool == Tool::Turn ? 15.f : 10.f, 10.f, 10.f };
                float delta[16]{};
                auto const changed = ImGuizmo::Manipulate(view, projection, operation, mode, model, delta, ctrl && tool != Tool::Scale ? snap : nullptr);
                auto const holding = ImGuizmo::IsUsing();
                if (holding && !Dragging)
                {
                    DragYaw = static_cast<float>(s.SelectedRotation.Yaw) * 360.f / 65536.f;
                    DragRotation = s.SelectedRotation;
                    DragDrawScale = s.SelectedDrawScale;
                    DragScale3D = s.SelectedDrawScale3D;
                    DragHandle = ImGuizmo::GetActiveHandleType();
                    Dragging = true;
                    DraggedPath = s.SelectedPath;
                }
                // The frame the button is released still moves the matrix: its change counts, then the drag ends. A move counts
                // every frame of the drag: for a translation, Manipulate returns whether this frame's step differs from the last
                // one, so a Ctrl-snapped drag at a steady pace (one 10-unit step a frame) would send its first step only, and a
                // release on such a frame would leave the actor short of the gizmo.
                auto const moving = tool == Tool::Move;
                if (Dragging && (holding || changed || moving))
                {
                    std::memcpy(DragModel, model, sizeof model);
                    // A turn or a scale sends only on a frame that changed the matrix. A move sends where the gizmo is: Post keeps
                    // the latest, and the game thread ignores one that doesn't move the actor (a click without motion edits nothing).
                    if (moving)
                    {
                        Post({ .Type = Command::Kind::Move, .X = model[12], .Y = model[13], .Z = model[14] });
                    }
                    else if (changed && pawnTurn)
                    {
                        // This frame's turn about the vertical, added up: the forward axis alone has no yaw for an actor pitched
                        // straight up or down.
                        DragYaw = std::remainder(DragYaw + std::atan2(delta[1], delta[0]) * 180.f / 3.14159265f, 360.f);
                        auto const yaw = static_cast<int>(std::lround(DragYaw * 65536.f / 360.f));
                        Post({ .Type = Command::Kind::Rotate, .Rotation = { DragRotation.Pitch, yaw, DragRotation.Roll } });
                    }
                    else if (changed && tool == Tool::Turn)
                    {
                        Post({ .Type = Command::Kind::Rotate, .Rotation = RotatorOf(DragModel) });
                    }
                    else if (changed && tool == Tool::Scale)
                    {
                        auto const scale = ScaleOf(DragModel, ctrl);
                        Post({ .Type = Command::Kind::Scale, .X = scale.X, .Y = scale.Y, .Z = scale.Z });
                    }
                }
                if (!holding && Dragging)
                {
                    // Ends the undo step; paused, the prop keeps the light of where it was until the world ticks once.
                    Post({ .Type = Command::Kind::EndDrag });
                    Dragging = false;
                }
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

        /// Clicks and the wheel that no window or arrow took. While the Lights tab is open or Alt is held, a click near a light's
        /// icon selects that light; any other click traces, so a light never takes a click meant for a prop (docs/07 §11).
        void HandleWorldInput(Snapshot const& s, float const* view, float const* projection, float width, float height)
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

            // Keys, unless a text field has them (the Props search).
            if (!io.WantTextInput && !Looking.load())
            {
                // Not mid-drag, on the gizmo or on a panel control: the drag's step is still open (another command would close it
                // half way), and the gizmo or the control would change under the mouse.
                auto const idle = !ImGuizmo::IsUsingAny() && !PanelEditing && !ImGui::IsAnyItemActive();
                if (idle && ImGui::IsKeyPressed(ImGuiKey_T, false)) Choose(s, Tool::Move);
                if (idle && ImGui::IsKeyPressed(ImGuiKey_R, false)) Choose(s, Tool::Turn);
                if (idle && ImGui::IsKeyPressed(ImGuiKey_G, false)) Choose(s, Tool::Scale);
                if (idle && s.Selected && s.SelectedUndeletable.empty() && ImGui::IsKeyPressed(ImGuiKey_Delete, false)) Post({ .Type = Command::Kind::Delete });
                if (idle && io.KeyCtrl && ImGui::IsKeyPressed(ImGuiKey_S, false)) Post({ .Type = Command::Kind::Save });
                if (idle && io.KeyCtrl && ImGui::IsKeyPressed(ImGuiKey_Z, false)) Post({ .Type = io.KeyShift ? Command::Kind::Redo : Command::Kind::Undo });
                if (idle && io.KeyCtrl && ImGui::IsKeyPressed(ImGuiKey_Y, false)) Post({ .Type = Command::Kind::Redo });
                // The game thread says why when the selection is not a prop it switches.
                if (idle && !io.KeyCtrl && s.Selected && ImGui::IsKeyPressed(ImGuiKey_L, false)) Post({ .Type = Command::Kind::PropLight });
            }

            if (free && !Looking.load() && ImGui::IsMouseClicked(ImGuiMouseButton_Left) && s.CanvasWidth > 0)
            {
                // With a tab open, only its icons; with Alt held, the lights' and the effects'.
                auto const icon = IconAt(s, view, projection, width, height, io.MousePos, LightsTabOpen || io.KeyAlt, EffectsTabOpen || io.KeyAlt);
                if (!icon.Path.empty()) Post({ .Type = icon.Effect ? Command::Kind::PickEffect : Command::Kind::PickLight, .Text = icon.Path });
                else Post({ .Type = Command::Kind::Pick, .X = io.MousePos.x * s.CanvasWidth / width, .Y = io.MousePos.y * s.CanvasHeight / height });
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
                    RefusedFor.clear();
                    ReleaseThumbnails();
                    PanelEditing = false;
                    PanelPath.clear();
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
            EndPanel(s);
            DrawProps(s, height);
            DrawDiagnostics(s, width, height);
            DrawIcons(s, view, projection, width, height);
            DrawNpcMarker(s, view, projection, width, height);
            DrawGizmo(s, view, projection, width, height);
            HandleWorldInput(s, view, projection, width, height);

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
                ReleaseThumbnails();
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
