#include "MeleBridge/Bridge.hpp"
#include "MeleBridge/Reflection.hpp"
#include "MeleBridge/SharedVersion.h"
#include "Common/Objects.hpp"

#include <format>
#include <optional>

// The status op (docs/formats.md §3.4): one cheap look at the running game, which an unattended test polls to tell when a load
// is over and the player can act. All of it runs inside one game-thread request. A section that can't be read is null, with
// why in "notes"; an access violation fails the whole request (RunOnGameThread's guard), and the client asks again.
namespace MeleBridge
{
    namespace
    {
        /// An object a scan found, checked again on use: objects come and go with levels. A freed slot is often taken by a new
        /// object at the same index and address, of another class more often than not (Editor.cpp, SelectionAlive), hence the
        /// class.
        struct Cached
        {
            int Index = -1;
            UObject* Object = nullptr;
            UClass* Class = nullptr;

            UObject* Get() const
            {
                auto& objects = *UObject::GObjObjects;
                if (Object == nullptr || Index >= static_cast<int>(objects.Count()) || objects.GetData()[Index] != Object) return nullptr;
                if (Object->Class != Class) return nullptr;
                return Common::IsDestroyed(Object) ? nullptr : Object;
            }
        };

        /// The first live object of a class (subclasses too) whose path starts with <paramref name="prefix"/> (any when empty).
        Cached FindFirst(UClass* cls, std::wstring const& prefix)
        {
            auto& objects = *UObject::GObjObjects;
            if (cls == nullptr) return {};
            for (int i = 0; i < static_cast<int>(objects.Count()); i++)
            {
                auto const object = objects.GetData()[i];
                if (object == nullptr || !object->IsA(cls) || Common::IsDefaultObject(object) || Common::IsDestroyed(object)) continue;
                if (!prefix.empty() && !StartsWithNoCase(PathOf(object), prefix)) continue;
                return { i, object, object->Class };
            }
            return {};
        }

        Cached World;
        std::wstring WorldMap;

        // ! Streaming levels
        // ========================================

        struct BoolField
        {
            int Offset = -1;
            DWORD Mask = 0;

            bool In(BYTE* base) const { return (*reinterpret_cast<DWORD*>(base + Offset) & Mask) != 0; }
        };

        /// LevelStreaming's fields where the engine laid them out; resolved once.
        struct LevelFields
        {
            BoolField IsVisible, LoadPending, UnloadPending, ShouldBeLoaded, ShouldBeVisible;
            int LoadedLevel = -1;
            int PackageName = -1;
        };
        std::optional<LevelFields> LevelLayout;

        LevelFields const& LevelStreamingFields()
        {
            if (LevelLayout) return *LevelLayout;
            auto const cls = ULevelStreaming::StaticClass();
            if (cls == nullptr) throw BridgeError("no LevelStreaming class is loaded");
            LevelFields fields;
            auto const flag = [cls](wchar_t const* name, BoolField& out) {
                if (!BoolFieldOf(cls, name, out.Offset, out.Mask)) throw BridgeError("LevelStreaming has no " + ToUtf8(name));
            };
            flag(L"bIsVisible", fields.IsVisible);
            flag(L"bHasLoadRequestPending", fields.LoadPending);
            flag(L"bHasUnloadRequestPending", fields.UnloadPending);
            flag(L"bShouldBeLoaded", fields.ShouldBeLoaded);
            flag(L"bShouldBeVisible", fields.ShouldBeVisible);
            fields.LoadedLevel = PropertyOffset(cls, L"LoadedLevel");
            fields.PackageName = PropertyOffset(cls, L"PackageName");
            if (fields.LoadedLevel < 0 || fields.PackageName < 0) throw BridgeError("LevelStreaming has no LoadedLevel or PackageName");
            LevelLayout = fields;
            return *LevelLayout;
        }

        /// A level is still on its way while a load or unload is pending, it should be loaded and isn't, or it should be shown or
        /// hidden and isn't yet.
        json Streaming(AWorldInfo* world)
        {
            auto const at = PropertyOffset(world->Class, L"StreamingLevels");
            if (at < 0) throw BridgeError(ToUtf8(NameOf(world->Class)) + " has no StreamingLevels");
            auto const& levels = *reinterpret_cast<TArray<ULevelStreaming*>*>(reinterpret_cast<BYTE*>(world) + at);
            auto const& f = LevelStreamingFields();
            int pending = 0;
            auto names = json::array();
            for (UINT i = 0; i < levels.Count(); i++)
            {
                auto const level = levels.GetData()[i];
                if (level == nullptr) continue;
                auto const base = reinterpret_cast<BYTE*>(level);
                auto const loaded = *reinterpret_cast<UObject**>(base + f.LoadedLevel) != nullptr;
                if (!f.LoadPending.In(base) && !f.UnloadPending.In(base) && !(f.ShouldBeLoaded.In(base) && !loaded)
                    && f.ShouldBeVisible.In(base) == f.IsVisible.In(base))
                    continue;
                pending++;
                if (names.size() < 5)
                {
                    auto const name = reinterpret_cast<SFXName*>(base + f.PackageName)->ToString();
                    names.push_back(ToUtf8(std::wstring(name.Chars(), name.Length())));
                }
            }
            return { { "levels", levels.Count() }, { "pending", pending }, { "pendingNames", names } };
        }

        // ! Loading movie
        // ========================================

        Cached Movie;
        /// After a scan that found no SFXLoadMovieManager: GetTickCount64 before which no scan runs again, so that polls during
        /// a load don't scan GObjects each time. MovieScanned: when that scan ran.
        ULONGLONG NoMovieUntil = 0;
        ULONGLONG MovieScanned = 0;
        constexpr ULONGLONG MovieRescan = 5000;

        /// Null when there is none, with a note that says whether this call looked or kept an earlier miss: a manager that came
        /// since then shows only at the next scan.
        json LoadingMovie(json& notes)
        {
            auto movie = Movie.Get();
            if (movie == nullptr)
            {
                auto const now = GetTickCount64();
                if (now < NoMovieUntil)
                {
                    notes.push_back(std::format("loadingMovie: no SFXLoadMovieManager at the last look, {:.1f} s ago; looked for again in {:.1f} s",
                                                static_cast<double>(now - MovieScanned) / 1000, static_cast<double>(NoMovieUntil - now) / 1000));
                    return nullptr;
                }
                Movie = FindFirst(USFXLoadMovieManager::StaticClass(), L"");
                movie = Movie.Object;
                if (movie == nullptr)
                {
                    MovieScanned = now;
                    NoMovieUntil = now + MovieRescan;
                    notes.push_back(std::format("loadingMovie: no SFXLoadMovieManager in memory; looked for again in {:.1f} s", static_cast<double>(MovieRescan) / 1000));
                    return nullptr;
                }
            }
            auto const playing = CallFunction(movie, L"IsLoadingMoviePlaying", json::array());
            return {
                { "object", ToUtf8(PathOf(movie)) },
                { "playing", playing.value("return", json(nullptr)) },
                { "state", GetProperty(movie, L"PlaybackState", 0) },
                // LE2 and LE3 have no bIsInLoad.
                { "inLoad", PropertyOffset(movie->Class, L"bIsInLoad") >= 0 ? GetProperty(movie, L"bIsInLoad", 0) : json(nullptr) },
            };
        }

#if MELEBRIDGE_PARTY_SELECT
        // ! Squad screen
        // ========================================

        /// An object property's or a return value's object; null for None.
        UObject* ObjectAt(json const& path) { return path.is_string() ? FindByPath(ToWide(path.get<std::string>())) : nullptr; }

        /// The squad screen's handler: the panel's default handler when it is a party selection one, else the first such script
        /// handler. The live class is BioSFHandler_PCPartySelection.
        UObject* PartyHandler(UObject* panel)
        {
            auto const cls = UBioSFHandler_PartySelection::StaticClass();
            if (cls == nullptr) return nullptr;
            if (auto const handler = ObjectAt(GetProperty(panel, L"m_DefaultHandler", 0)); handler != nullptr && handler->IsA(cls)) return handler;
            for (auto const& item : GetProperty(panel, L"ScriptHandlers", 1))
            {
                // Past 64 items the list ends in "... N more", which is no path.
                if (!item.is_string() || item.get<std::string>().starts_with("... ")) continue;
                if (auto const handler = ObjectAt(item); handler != nullptr && handler->IsA(cls)) return handler;
            }
            return nullptr;
        }

        /// The open squad screen. The movie sets nMembersAvailable to 0 when it resets and counts it up as it adds each card, so
        /// a count above 0 says the screen has run its initialisation (the handler's DoInitialization).
        json PartySelect(UObject* panel)
        {
            auto const handler = PartyHandler(panel);
            json members = nullptr;
            json varPath = nullptr;
            for (auto const path : { "_root.nMembersAvailable", "nMembersAvailable" })
            {
                if (!CallFunction(panel, L"IsAvailable", json::array({ path })).value("return", false)) continue;
                members = CallFunction(panel, L"GetVariableInt", json::array({ path })).value("return", json(nullptr));
                varPath = path;
                break;
            }
            return {
                { "panel", ToUtf8(PathOf(panel)) },
                { "handler", handler ? json(ToUtf8(PathOf(handler))) : json(nullptr) },
                { "handlerClass", handler ? json(ToUtf8(NameOf(handler->Class))) : json(nullptr) },
                { "membersAvailable", members },
                { "varPath", varPath },
            };
        }
#endif
    }

    AWorldInfo* CurrentWorldInfo()
    {
        auto const map = MapName();
        if (map.empty()) return nullptr;
        // A reload of the same map keeps the name: the path says the cached object is still that map's persistent world.
        auto const prefix = map + L".TheWorld.PersistentLevel.";
        if (auto const world = World.Get(); world != nullptr && WorldMap == map && StartsWithNoCase(PathOf(world), prefix))
            return static_cast<AWorldInfo*>(world);
        World = FindFirst(ABioWorldInfo::StaticClass(), prefix);
        WorldMap = map;
        return static_cast<AWorldInfo*>(World.Object);
    }

    json Status()
    {
        auto notes = json::array();
        // A section that can't be read is null, with why in notes.
        auto const section = [&notes](char const* name, auto&& read) -> json {
            try
            {
                return read();
            }
            catch (std::exception const& e)
            {
                notes.push_back(std::string(name) + ": " + e.what());
                return nullptr;
            }
        };

        auto reply = KeepTickingState();
        auto const map = MapName();
        reply["bridge"] = VERSION_STRING_A;
        reply["map"] = ToUtf8(map);

        auto const world = CurrentWorldInfo();
        if (world == nullptr) notes.push_back("no BioWorldInfo in " + (map.empty() ? std::string("memory (no map)") : ToUtf8(map)));
        reply["world"] = world ? json(ToUtf8(PathOf(world))) : json(nullptr);
        reply["begunPlay"] = world ? json(world->bBegunPlay != 0) : json(nullptr);
        reply["bPlayersOnly"] = world ? json(world->bPlayersOnly != 0) : json(nullptr);
        reply["pausedByFocusLoss"] = world ? section("pausedByFocusLoss", [world] { return json(PausedByFocusLoss(world)); }) : json(nullptr);
        reply["streaming"] = world ? section("streaming", [world] { return Streaming(world); }) : json(nullptr);

        auto const pc = CurrentPlayerController();
        if (pc == nullptr) notes.push_back("no player controller");
        reply["pc"] = pc ? json(ToUtf8(PathOf(pc))) : json(nullptr);
        reply["pawn"] = pc ? section("pawn", [pc] { return GetProperty(pc, L"Pawn", 0); }) : json(nullptr);

        reply["loadingMovie"] = section("loadingMovie", [&notes] { return LoadingMovie(notes); });

#if MELEBRIDGE_PARTY_SELECT
        // The squad screen: GetPanelByTag('PartySelect') on the player's GUI manager (BioPlayerController's returns the
        // MassEffectGuiManager).
        UObject* panel = nullptr;
        reply["partySelectOpen"] = pc == nullptr ? json(nullptr) : section("partySelectOpen", [pc, &panel] {
            auto const gui = ObjectAt(CallFunction(pc, L"GetScaleFormManager", json::array()).value("return", json(nullptr)));
            if (gui == nullptr) throw BridgeError("the player controller has no GUI manager");
            panel = ObjectAt(CallFunction(gui, L"GetPanelByTag", json::array({ "PartySelect" })).value("return", json(nullptr)));
            return json(panel != nullptr);
        });
        reply["partySelect"] = panel == nullptr ? json(nullptr) : section("partySelect", [panel] { return PartySelect(panel); });
#else
        reply["partySelectOpen"] = nullptr;
        reply["partySelect"] = nullptr;
        notes.push_back("partySelectOpen: not read in this game (no BioSFHandler_PartySelection)");
#endif

        reply["notes"] = notes;
        return reply;
    }
}
