#include "MeleBridge/Bridge.hpp"
#include "MeleBridge/Editor.hpp"
#include "MeleBridge/Reflection.hpp"
#include "MeleBridge/SharedVersion.h"
#include "Common/DefaultLogger.hpp"


SPI_PLUGINSIDE_SUPPORT(SDK_TARGET_NAME_W ASI_NAME_NO_SPACE_W, DEVELOPER_W, L"" VERSION_STRING_W, SPI_GAME_SDK_TARGET, SPI_VERSION_ANY);
SPI_PLUGINSIDE_POSTLOAD;
SPI_PLUGINSIDE_ASYNCATTACH;


SPI_IMPLEMENT_ATTACH
{
    // Logs\MeleBridge.log next to the game's executable, rewritten at each start.
    Common::SetupDefaultLogger(SDK_TARGET_NAME_A, ASI_NAME_NO_SPACE_A);
    ::LESDK::Initializer Init{ InterfacePtr, SDK_TARGET_NAME_A ASI_NAME_NO_SPACE_A };

    ::MeleBridge::InitializeGlobals(Init);
    ::MeleBridge::InstallHooks(Init);
    ::MeleBridge::Editor::InstallInput(Init);
    ::MeleBridge::Editor::InstallOverlay(Init);
    ::MeleBridge::StartPipeServer();

    LEASI_INFO("MeleBridge {} ready", VERSION_STRING_A);
    LEASI_FLUSH();
    return true;
}

SPI_IMPLEMENT_DETACH
{
    LEASI_UNUSED(InterfacePtr);
    Common::ShutdownLogger();
    return true;
}
