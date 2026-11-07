#pragma once

#include <LESDK/Headers.hpp>
#include <LESDK/Init.hpp>
#include <SPI.h>
#include "Common/Base.hpp"
#include "Common/json.hpp"
#include "MeleBridge/game/Game.hpp"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

namespace MeleBridge
{
    using json = nlohmann::json;

    /// A request the bridge refuses or cannot do; the message goes back to the client as {"ok":false,"error":...}.
    struct BridgeError : std::runtime_error
    {
        using std::runtime_error::runtime_error;
    };

    // ! GameThread.cpp
    // ========================================

    void InstallHooks(::LESDK::Initializer& Init);

    /// Runs work on the game thread (from the ProcessEvent hook) and returns its result. Throws BridgeError when the game
    /// thread does not pick it up in time; work that has not started by then never runs. Work that started runs to its end:
    /// when it is still running 1.5 s after the timeout, this returns {"ok":false,"running":true,"error":...} instead, and
    /// drops the result the work hands over later. A throw or a fault (access violation, integer division by zero) in the
    /// work becomes {"ok":false,"error":...}.
    json RunOnGameThread(std::function<json()> work, std::chrono::milliseconds timeout);

    /// A watched function: ProcessEvent gets the most derived override (SeqAct_Log.Activated for SequenceOp.Activated), so a
    /// call matches by name on an object of the declaring class or a subclass. For a function declared in a state, Owner is the
    /// class that holds the state, and a call matches only when the function it runs is declared in a state of that name too
    /// (a subclass's override of the state keeps the name). A watch on a class function also sees its states' overrides.
    struct Watch
    {
        SFXName Name;
        UClass* Owner;
        bool InState = false;
        SFXName State{};
    };

    /// An event-stream client: the game thread queues one JSON line per watched ProcessEvent call; the pipe thread writes them.
    /// Calls made while a request runs on the game thread, and the in-game editor's own calls, are not queued (the ProcessEvent
    /// hook in GameThread.cpp says why).
    struct Subscriber
    {
        std::vector<Watch> Watches;
        std::wstring ObjectContains;
        bool WithParams = true;

        std::mutex Lock;
        std::condition_variable Ready;
        std::deque<std::string> Lines;
        size_t Dropped = 0;
        std::atomic<bool> Closed{ false };
    };

    /// Game thread only.
    void AddSubscriber(std::shared_ptr<Subscriber> subscriber);

    /// The keep_ticking test switch, for unattended tests: off at every game start. While on, BioWorldInfo.Tick never takes its
    /// focus-loss pause, so the world and Kismet run with the game window in the background. Game thread: resolves the
    /// BioWorldInfo fields it needs (BridgeError when one is missing), turns the switch on, and ends a focus-loss pause already
    /// in place on <paramref name="world"/> (may be null). Returns whether it ended one.
    bool KeepTickingOn(AWorldInfo* world);

    /// Any thread: the switch off. A Tick running at that moment still puts back what it changed.
    void KeepTickingOff();

    /// Any thread: {keepTicking, worldTicks, focusPausesCleared, timerRaises, gameStarted}. The counters run since the game
    /// started, and only while the switch is on. gameStarted is the game process's start time (UTC, ISO 8601), the same at
    /// every call until the game restarts with the switch off.
    json KeepTickingState();

    /// Game thread: a BioWorldInfo's m_bPausedByFocusLoss, through the field reflection gives (BridgeError when it has none).
    bool PausedByFocusLoss(AWorldInfo* world);

    // ! Console.cpp
    // ========================================

    /// Game thread. Runs the command; it returns no output on any game (see Console.cpp).
    json RunConsoleCommand(std::wstring const& command);

    /// Game thread. The current map's live player controller; else the first one in memory; null when there is none.
    APlayerController* CurrentPlayerController();

    // ! Status.cpp
    // ========================================

    /// Game thread. The current map's BioWorldInfo (its persistent level's), or null.
    AWorldInfo* CurrentWorldInfo();

    /// Game thread. The status op's reply, "ok" aside: map, world, player, streaming, loading movie, squad screen, switch.
    json Status();

    // ! Pipe.cpp
    // ========================================

    void StartPipeServer();

    // ! Ops.cpp
    // ========================================

    /// Answers one request. A "subscribe" request also fills <paramref name="subscription"/>: the connection then streams events.
    json HandleRequest(json const& request, std::shared_ptr<Subscriber>& subscription);

    // ! Text
    // ========================================

    std::string ToUtf8(std::wstring_view text);
    std::wstring ToWide(std::string_view text);
}
