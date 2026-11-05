#include "MeleBridge/Bridge.hpp"
#include "MeleBridge/Reflection.hpp"
#include "Common/Objects.hpp"

// Console commands through the player's console chain (player execs, cheat manager, then the engine), like typing them.
// The op hands no output back on any game. On LE1 ConsoleCommand returns an empty string, and with the DebugLogger ASI installed
// its Logf hooks keep every printed line for its log without passing it on. On every game the client reads the output from
// that log (LiveBridge.Console).
namespace MeleBridge
{
    namespace
    {
        /// The last live controller found, checked again on use: a map change leaves the old one dying in GObjects for a while,
        /// and a reload of the same map keeps the name while the old controller is freed. Its slot is often taken by a new object
        /// at the same index and address, of another class more often than not (Editor.cpp, SelectionAlive), hence the class.
        int CachedIndex = -1;
        APlayerController* Cached = nullptr;
        UClass* CachedClass = nullptr;
        std::wstring CachedMap;

        bool Live(APlayerController* pc) { return !Common::IsDestroyed(pc) && !pc->bDeleteMe; }
    }

    APlayerController* CurrentPlayerController()
    {
        auto& objects = *UObject::GObjObjects;
        auto const map = MapName();
        if (Cached != nullptr && CachedIndex < static_cast<int>(objects.Count()) && objects.GetData()[CachedIndex] == Cached
            && Cached->Class == CachedClass && CachedMap == map && Live(Cached))
            return Cached;
        Cached = nullptr;

        // The current map's live controller. Without one (no map yet, or none of its controllers is up), the first one, as
        // before MeleBridge 3: a dying controller of the old map is picked only when nothing else is there.
        auto const cls = APlayerController::StaticClass();
        auto const prefix = map + L".TheWorld.";
        APlayerController* first = nullptr;
        for (int i = 0; i < static_cast<int>(objects.Count()); i++)
        {
            auto const object = objects.GetData()[i];
            if (object == nullptr || !object->IsA(cls) || Common::IsDefaultObject(object)) continue;
            auto const pc = static_cast<APlayerController*>(object);
            if (first == nullptr) first = pc;
            if (map.empty() || !Live(pc) || !StartsWithNoCase(PathOf(pc), prefix)) continue;
            CachedIndex = i;
            Cached = pc;
            CachedClass = pc->Class;
            CachedMap = map;
            return pc;
        }
        return first;
    }

    json RunConsoleCommand(std::wstring const& command)
    {
        auto const pc = CurrentPlayerController();
        if (pc == nullptr) throw BridgeError("no player controller");
        CallFunction(pc, L"ConsoleCommand", json::array({ ToUtf8(command), false }));
        return json::object();
    }
}
