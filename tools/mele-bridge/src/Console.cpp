#include "MeleBridge/Bridge.hpp"
#include "MeleBridge/Reflection.hpp"

// Console commands through the player's console chain (player execs, cheat manager, then the engine), like typing them.
// LE1 hands no output back: ConsoleCommand returns an empty string, and with the DebugLogger ASI installed its Logf hooks keep
// every printed line for its log without passing it on. The client reads the output from that log (LiveBridge.Console).
namespace MeleBridge
{
    namespace
    {
        constexpr unsigned long long RF_ClassDefaultObject = 0x200;

        APlayerController* PlayerController()
        {
            auto& objects = *UObject::GObjObjects;
            for (UINT i = 0; i < objects.Count(); i++)
            {
                auto const object = objects.GetData()[i];
                if (object != nullptr && object->IsA(APlayerController::StaticClass()) && (object->ObjectFlags & RF_ClassDefaultObject) == 0)
                    return static_cast<APlayerController*>(object);
            }
            throw BridgeError("no player controller");
        }
    }

    json RunConsoleCommand(std::wstring const& command)
    {
        CallFunction(PlayerController(), L"ConsoleCommand", json::array({ ToUtf8(command), false }));
        return json::object();
    }
}
