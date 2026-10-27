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
// It also reads the prop catalog mele-mcp builds (catalog.json and its thumbnail PNGs) for the Props window's grid.
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

        enum class Tool { Move, Turn };
        Tool Current = Tool::Move;
        Tool LastTool = Tool::Move;
        /// A turn's yaw in degrees: the selection's at the start of the drag plus each frame's turn.
        float DragYaw = 0;
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

        /// An actor's transform as UE3's FRotationTranslationMatrix: the axes as rows, then the location.
        void ModelMatrix(FVector location, FRotator const& rotation, float* m)
        {
            Axes(rotation, m, m + 4, m + 8);
            m[3] = m[7] = m[11] = 0;
            m[12] = location.X;
            m[13] = location.Y;
            m[14] = location.Z;
            m[15] = 1;
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
                ImGui::Checkbox("Props", &ShowProps);
                ImGui::SameLine();
                ImGui::TextDisabled("|");
                ImGui::SameLine();
                // Not mid-drag: the gizmo would change under the mouse.
                ImGui::BeginDisabled(ImGuizmo::IsUsingAny());
                if (ImGui::RadioButton("Move (T)", Current == Tool::Move)) Current = Tool::Move;
                ImGui::SameLine();
                if (ImGui::RadioButton("Turn (R)", Current == Tool::Turn)) Current = Tool::Turn;
                ImGui::EndDisabled();
                ImGui::SameLine();
                ImGui::TextDisabled("|");
                ImGui::SameLine();
                ImGui::BeginDisabled(!s.Selected || !s.SelectedUndeletable.empty());
                if (ImGui::Button("Delete")) Post({ .Type = Command::Kind::Delete });
                ImGui::EndDisabled();
                if (s.Selected && !s.SelectedUndeletable.empty() && ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled))
                    ImGui::SetTooltip("%s", s.SelectedUndeletable.c_str());
                ImGui::SameLine();
                auto const saveLabel = s.Unsaved > 0 ? std::format("Save ({})###Save", s.Unsaved) : std::string("Save###Save");
                if (ImGui::Button(saveLabel.c_str())) Post({ .Type = Command::Kind::Save });
                if (ImGui::IsItemHovered()) ImGui::SetTooltip("Ctrl+S. Writes the session file; editor_import in mele-mcp puts it in the level YAML and builds.");
                ImGui::SameLine();
                ImGui::TextDisabled("|");
                ImGui::SameLine();
                ImGui::Checkbox("Diagnostics", &ShowDiagnostics);
                ImGui::SameLine();
                if (ImGui::Button("Close (F8)")) Post({ .Type = Command::Kind::Close });

                if (s.FreeCam)
                    ImGui::TextDisabled("Right mouse: look   WASD: move   Q/E: down/up   Shift: fast   Wheel: speed (%.0f)   Left click: select", s.Speed);
                else
                    ImGui::TextDisabled("Left click: select   Arrows: drag to move or turn   Ctrl: snap (10 units, 15 degrees)   Del: delete");

                // The game thread's last word, for a few seconds.
                LARGE_INTEGER now, frequency;
                QueryPerformanceCounter(&now);
                QueryPerformanceFrequency(&frequency);
                if (s.StatusFrame != StatusSeen)
                {
                    StatusSeen = s.StatusFrame;
                    StatusSince = now;
                }
                if (!s.Status.empty() && now.QuadPart - StatusSince.QuadPart < 8 * frequency.QuadPart)
                {
                    auto const refused = s.Status.starts_with("not ") || s.Status.starts_with("the engine refused");
                    ImGui::TextColored(refused ? ImVec4(1, 0.6f, 0.3f, 1) : ImVec4(0.5f, 1, 0.6f, 1), "%s", s.Status.c_str());
                }
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
                ImGui::Text("Class: %s%s%s", s.SelectedClass.c_str(), s.SelectedStatic ? " (static)" : "", s.SelectedSpawned ? " (spawned)" : "");
                ImGui::Text("Tag: %s", s.SelectedTag.c_str());
                ImGui::Text("X %.1f   Y %.1f   Z %.1f   Yaw %.1f", s.SelectedLocation.X, s.SelectedLocation.Y, s.SelectedLocation.Z,
                            static_cast<float>(static_cast<short>(s.SelectedRotation.Yaw & 0xFFFF)) * 360.f / 65536.f);
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

        /// Props: the catalog as a grid of thumbnails, and the meshes in memory as a list.
        void DrawProps(Snapshot const& s, float height)
        {
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
            if (Current != LastTool)
            {
                ResetGizmo();
                LastTool = Current;
            }

            float model[16];
            ModelMatrix(s.SelectedLocation, s.SelectedRotation, model);
            // While dragging, our own copy leads: the game applies each move a frame or two later.
            if (Dragging) std::memcpy(model, DragModel, sizeof model);
            ImGuizmo::SetOrthographic(false);
            ImGuizmo::SetDrawlist(ImGui::GetBackgroundDrawList());
            ImGuizmo::SetRect(0, 0, width, height);
            // Ctrl snaps: 10 units, or 15 degrees. Turning is around the vertical only: the level YAML keeps a yaw.
            auto const turn = Current == Tool::Turn;
            float const snap[3] = { turn ? 15.f : 10.f, 10.f, 10.f };
            float delta[16];
            ImGuizmo::Manipulate(view, projection, turn ? ImGuizmo::ROTATE_Z : ImGuizmo::TRANSLATE, ImGuizmo::WORLD, model, delta,
                                 ImGui::GetIO().KeyCtrl ? snap : nullptr);
            if (ImGuizmo::IsUsing())
            {
                if (!Dragging) DragYaw = static_cast<float>(s.SelectedRotation.Yaw) * 360.f / 65536.f;
                Dragging = true;
                DraggedPath = s.SelectedPath;
                std::memcpy(DragModel, model, sizeof model);
                if (turn)
                {
                    // This frame's turn about the vertical, added up: the forward axis alone has no yaw for an actor pitched
                    // straight up or down.
                    DragYaw = std::remainder(DragYaw + std::atan2(delta[1], delta[0]) * 180.f / 3.14159265f, 360.f);
                    Post({ .Type = Command::Kind::Rotate, .X = DragYaw });
                }
                else
                {
                    Post({ .Type = Command::Kind::Move, .X = model[12], .Y = model[13], .Z = model[14] });
                }
            }
            else
            {
                // The drag ended: paused, the prop keeps the light of where it was until the world ticks.
                if (Dragging) Post({ .Type = Command::Kind::TickWorld });
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

            // Keys, unless a text field has them (the Props search).
            if (!io.WantTextInput && !Looking.load())
            {
                if (!ImGuizmo::IsUsingAny() && ImGui::IsKeyPressed(ImGuiKey_T, false)) Current = Tool::Move;
                if (!ImGuizmo::IsUsingAny() && ImGui::IsKeyPressed(ImGuiKey_R, false)) Current = Tool::Turn;
                if (s.Selected && s.SelectedUndeletable.empty() && ImGui::IsKeyPressed(ImGuiKey_Delete, false)) Post({ .Type = Command::Kind::Delete });
                if (io.KeyCtrl && ImGui::IsKeyPressed(ImGuiKey_S, false)) Post({ .Type = Command::Kind::Save });
            }

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
                    ReleaseThumbnails();
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
            DrawProps(s, height);
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
