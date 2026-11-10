#include "MeleBridge/Bridge.hpp"
#include "MeleBridge/Editor.hpp"
#include "MeleBridge/Reflection.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <format>
#include <future>
#include <thread>
#include <vector>

namespace MeleBridge
{
    // ! Jobs
    // ========================================

    namespace
    {
        /// A request's life. The pipe thread queues it (Queued); the game thread takes it (Running) and marks it Done just before it
        /// hands the result over. The pipe thread gives up on a Queued job at its timeout (Cancelled: it never runs), and on a Running
        /// one RunningGrace later (Abandoned: it runs to its end, and its result is dropped). Each move is one compare-exchange, so
        /// the two threads never both win the same job.
        enum class JobState : int { Queued, Running, Done, Cancelled, Abandoned };

        /// How long a request the game thread took before its timeout ended gets on top of that timeout. Then the client hears that
        /// it is still running, instead of nothing. mele-mcp waits 4 s past the timeout for a reply line (older builds 2 s): both
        /// get this answer before they give up.
        constexpr std::chrono::milliseconds RunningGrace{ 1500 };

        struct Job
        {
            std::function<json()> Work;
            std::promise<json> Result;
            std::atomic<JobState> State{ JobState::Queued };
        };

        std::mutex JobsLock;
        std::deque<std::shared_ptr<Job>> Jobs;
        std::atomic<bool> HasJobs{ false };

        /// Set while the game thread runs jobs: the work calls ProcessEvent itself, and a job must not start inside another.
        thread_local bool InJobs = false;

        /// InJobs for the length of a scope, put back however the scope ends: left set, it would keep every later job from running.
        struct InJobsScope
        {
            InJobsScope() { InJobs = true; }
            ~InJobsScope() { InJobs = false; }
            InJobsScope(InJobsScope const&) = delete;
            InJobsScope& operator=(InJobsScope const&) = delete;
        };

        /// Set while a BioWorldInfo.Tick runs with keep_ticking's raised timer (CanPause() false): jobs wait for the first
        /// ProcessEvent after it, so a request (the pause menu, a save) never sees that timer.
        thread_local bool InRaisedTick = false;

        struct GuardContext
        {
            std::function<json()>* Work;
            json Result;
            std::exception_ptr Error;
        };

        void RunWork(void* context)
        {
            auto* c = static_cast<GuardContext*>(context);
            try
            {
                c->Result = (*c->Work)();
            }
            catch (...)
            {
                c->Error = std::current_exception();
            }
        }

        /// The faults a request may raise in the game code it calls that fail the request instead of the game: they leave the game
        /// as an access violation does. A stack overflow is not one: its guard page is gone afterwards, and the next overflow would
        /// end the process without a word.
        bool Recoverable(DWORD code)
        {
            return code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_IN_PAGE_ERROR || code == EXCEPTION_INT_DIVIDE_BY_ZERO
                || code == EXCEPTION_INT_OVERFLOW;
        }

        /// A fault Recoverable takes, in the words of the request's error.
        char const* FaultName(DWORD code)
        {
            switch (code)
            {
            case EXCEPTION_ACCESS_VIOLATION: return "access violation";
            case EXCEPTION_IN_PAGE_ERROR: return "in-page error";
            case EXCEPTION_INT_DIVIDE_BY_ZERO: return "integer division by zero";
            default: return "integer overflow";
            }
        }

        /// An access violation in a request (a stale pointer, a wrong guess about memory) fails the request instead of the game, and
        /// so do the other faults Recoverable names. Returns 0 when the work ran, else the fault's code.
        /// No C++ object may live in this frame: __try cannot unwind one.
        DWORD RunGuarded(void (*work)(void*), void* context)
        {
            __try
            {
                work(context);
                return 0;
            }
            __except (Recoverable(GetExceptionCode()) ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH)
            {
                return GetExceptionCode();
            }
        }

        /// Hands a job's result over. One the pipe thread abandoned meanwhile still gets it, as no one waits for it any more: setting
        /// a promise whose future is gone is harmless, and the Job lives on in the batch until then.
        void Finish(Job& job, json result)
        {
            auto expected = JobState::Running;
            if (!job.State.compare_exchange_strong(expected, JobState::Done))
                LEASI_WARN("a request finished after its client was told it was still running: its reply is dropped");
            job.Result.set_value(std::move(result));
        }

        void RunJobs()
        {
            std::deque<std::shared_ptr<Job>> batch;
            {
                std::scoped_lock lock{ JobsLock };
                batch.swap(Jobs);
                HasJobs.store(false, std::memory_order_relaxed);
            }
            for (auto& job : batch)
            {
                auto expected = JobState::Queued;
                if (!job->State.compare_exchange_strong(expected, JobState::Running)) continue;

                GuardContext context{ &job->Work, {}, {} };
                if (auto const fault = RunGuarded(RunWork, &context); fault != 0)
                {
                    LEASI_ERROR("{} while running a request", FaultName(fault));
                    Finish(*job, json{ { "ok", false }, { "error", std::string(FaultName(fault)) + " in the game while running the request" } });
                }
                else if (context.Error)
                {
                    // Every throw ends in a reply: one that escaped here would leave the pipe thread waiting and leave the game.
                    std::string error;
                    try
                    {
                        std::rethrow_exception(context.Error);
                    }
                    catch (std::exception const& e)
                    {
                        error = e.what();
                    }
                    catch (...)
                    {
                        error = "the request threw something that is not a std::exception";
                    }
                    Finish(*job, json{ { "ok", false }, { "error", error } });
                }
                else
                {
                    Finish(*job, std::move(context.Result));
                }
            }
        }
    }

    json RunOnGameThread(std::function<json()> work, std::chrono::milliseconds timeout)
    {
        auto job = std::make_shared<Job>();
        job->Work = std::move(work);
        auto result = job->Result.get_future();
        {
            std::scoped_lock lock{ JobsLock };
            Jobs.push_back(job);
            HasJobs.store(true, std::memory_order_release);
        }

        if (result.wait_for(timeout) == std::future_status::ready) return result.get();

        auto expected = JobState::Queued;
        if (job->State.compare_exchange_strong(expected, JobState::Cancelled))
        {
            throw BridgeError("the game thread did not take the request in time (game loading, or not running script?)");
        }
        // Started: it can't be stopped, so it gets a little longer to finish. Past that the client hears it is still running.
        if (result.wait_for(RunningGrace) == std::future_status::ready) return result.get();
        expected = JobState::Running;
        if (!job->State.compare_exchange_strong(expected, JobState::Abandoned)) return result.get(); // Done just now: being set.
        LEASI_WARN("a request was still running {} ms after its {} ms timeout: answered as still running", RunningGrace.count(), timeout.count());
        // "did not answer": the words clients already take for a reply lost to a load (scenario.py's LOADING).
        return json{
            { "ok", false },
            { "running", true },
            { "error", std::format("the game thread did not answer the request in time: it started, and was still running {} ms after its {} ms "
                                   "timeout, so it may still complete; check its effect before sending it again", RunningGrace.count(), timeout.count()) },
        };
    }

    // ! Event stream
    // ========================================

    namespace
    {
        /// Game thread only, like everything that reads it.
        std::vector<std::shared_ptr<Subscriber>> Subscribers;
        std::atomic<bool> Watching{ false };
        auto const Started = std::chrono::steady_clock::now();
        constexpr size_t MaxQueued = 10000;

        void Publish(UObject* context, UFunction* function, void* parms)
        {
            bool anyClosed = false;
            for (auto const& sub : Subscribers)
            {
                if (sub->Closed.load(std::memory_order_relaxed))
                {
                    anyClosed = true;
                    continue;
                }
                auto const watched = std::any_of(sub->Watches.begin(), sub->Watches.end(), [&](Watch const& w) {
                    if (std::memcmp(&w.Name, &function->Name, sizeof(SFXName)) != 0 || !context->IsA(w.Owner)) return false;
                    if (!w.InState) return true;
                    // A state's function: the call must run one declared in a state of that name, not the class's own function
                    // or another state's, which share its name on the same objects.
                    auto const outer = function->Outer;
                    return outer != nullptr && outer->IsA(UState::StaticClass()) && !outer->IsA(UClass::StaticClass())
                        && std::memcmp(&w.State, &outer->Name, sizeof(SFXName)) == 0;
                });
                if (!watched) continue;

                auto const path = PathOf(context);
                if (!sub->ObjectContains.empty() && !ContainsNoCase(path, sub->ObjectContains)) continue;

                auto const seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - Started).count();
                json event{
                    { "t", std::round(seconds * 1000) / 1000 },
                    { "function", ToUtf8(PathOf(function)) },
                    { "object", ToUtf8(path) },
                    { "class", ToUtf8(NameOf(context->Class)) },
                };
                if (sub->WithParams && parms != nullptr) event["params"] = ExportParams(function, static_cast<BYTE*>(parms));
                if (context->IsA(USequenceOp::StaticClass()))
                {
                    // The input pin that fired, as Kismet Logger reports it.
                    for (auto& link : static_cast<USequenceOp*>(context)->InputLinks)
                    {
                        if (link.bHasImpulse) event["pin"] = ToUtf8(std::wstring(link.LinkDesc.Chars(), link.LinkDesc.Length()));
                    }
                }

                {
                    std::scoped_lock lock{ sub->Lock };
                    if (sub->Lines.size() < MaxQueued) sub->Lines.push_back(event.dump(-1, ' ', false, json::error_handler_t::replace));
                    else sub->Dropped++;
                }
                sub->Ready.notify_one();
            }
            if (anyClosed)
            {
                std::erase_if(Subscribers, [](auto const& s) { return s->Closed.load(std::memory_order_relaxed); });
                Watching.store(!Subscribers.empty(), std::memory_order_relaxed);
            }
        }

        /// Ends every watch: each client gets a last line that says why, then Stream (Pipe.cpp) closes its connection. Game
        /// thread. The last line is best effort; the closed connection always follows.
        void EndWatches(std::string const& why)
        {
            LEASI_ERROR("the event stream ended: {}", why.c_str());
            // "dropped": 0 keeps the line out of a client's events: mele-mcp counts every line without that key as a watched call.
            auto const line = json{ { "dropped", 0 }, { "error", "the watch ended: " + why } }.dump(-1, ' ', false, json::error_handler_t::replace);
            for (auto const& sub : Subscribers)
            {
                {
                    // The pipe thread holds this lock only while it takes the queued lines. A bounded wait: the game thread
                    // must never hang here, whatever a fault left behind.
                    std::unique_lock lock{ sub->Lock, std::try_to_lock };
                    for (int i = 0; i < 100 && !lock.owns_lock(); i++)
                    {
                        std::this_thread::sleep_for(std::chrono::milliseconds(1));
                        if (lock.try_lock()) break;
                    }
                    if (lock.owns_lock()) sub->Lines.push_back(line);
                    // Set before the lock is let go (when it was taken): Stream reads the flag under the same lock, so it
                    // never sees the flag without the line.
                    sub->Closed.store(true);
                }
                sub->Ready.notify_one();
            }
            Subscribers.clear();
            Watching.store(false, std::memory_order_relaxed);
        }

        struct PublishContext
        {
            UObject* Context;
            UFunction* Function;
            void* Parms;
            bool Threw;
        };

        void PublishWork(void* context)
        {
            auto* c = static_cast<PublishContext*>(context);
            try
            {
                Publish(c->Context, c->Function, c->Parms);
            }
            catch (...)
            {
                c->Threw = true;
            }
        }

        /// Publish, for the ProcessEvent hook: nothing it does may leave the hook into the game's own frames. A fault RunGuarded
        /// takes (a parameter or a Kismet op read at a wrong address) or a C++ throw ends the watches instead of the game.
        void PublishGuarded(UObject* context, UFunction* function, void* parms)
        {
            PublishContext c{ context, function, parms, false };
            auto const fault = RunGuarded(PublishWork, &c);
            if (fault == 0 && !c.Threw) return;
            try
            {
                EndWatches(fault != 0 ? std::string(FaultName(fault)) + " in the game while reading a watched call"
                                      : std::string("an error while reading a watched call"));
            }
            catch (...)
            {
                // Out of memory while saying so: the watches still end, without their last line.
                for (auto const& sub : Subscribers) sub->Closed.store(true);
                Subscribers.clear();
                Watching.store(false, std::memory_order_relaxed);
            }
        }
    }

    void AddSubscriber(std::shared_ptr<Subscriber> subscriber)
    {
        Subscribers.push_back(std::move(subscriber));
        Watching.store(true, std::memory_order_relaxed);
    }

    // ! Keep ticking (test switch)
    // ========================================

    // BioWorldInfo.Tick (SFXGame) pauses the world when the window loses focus: it sets m_bPausedByFocusLoss and bPlayersOnly
    // when !bPlayersOnly && !HasFocus() && GameModeManager.CanPause(), and lifts that pause only once the focus is back.
    // HasFocus is a native script calls, which never passes through ProcessEvent; Tick is an event, which does. CanPause
    // (SFXGameModeBase) is false while BioWorldInfo.m_fNoBrowserWheelTimer > 0, and Tick first counts that timer down by its
    // fDeltaT while it is > 0 (1e9 minus a frame's time is 1e9 in a float), then asks CanPause. So while the switch is on, a
    // timer this Tick would bring to 0 or below is raised to 1e9 for the length of the Tick only, and put back to 0 afterwards
    // (vanilla would have left it at 0 or below too): the pause menu (ShowMenuGui asks CanPause too) behaves as vanilla outside
    // it. EndConversation sets the timer to 1.0 (StartNoBrowserWheelTimer) for its own reasons: that value counts down as
    // vanilla until its last Tick, which gets the raise.
    //
    // LE2 and LE3 have no such timer, and their CanPause is only the mode's bAllowPauseMenu. Their Tick asks
    // m_bAllowBrowserWheel first, so while the switch is on that flag is off for the length of each Tick and put back afterwards
    // (a level that holds it off pauses on nothing anyway). In script only ShowBrowserWheel (the pause menu's opener) reads it
    // beside Tick, and requests wait as above. Tick's timer callbacks (BioTimerList.Tick) and its native TickLocal run in that
    // window and were not audited: one that opens the pause menu would find it held, and one that writes the flag false
    // (BioSeqAct_SetBrowserWheelAvailable is the only writer in script) would be undone.

    namespace
    {
        std::atomic<bool> KeepTicking{ false };
        std::atomic<unsigned long long> WorldTicks{ 0 };
        std::atomic<unsigned long long> FocusPausesCleared{ 0 };
        std::atomic<unsigned long long> TimerRaises{ 0 };

        /// BioWorldInfo's fields where the engine laid them out (Editor.cpp says why the SDK's C++ layout is not trusted). Game
        /// thread only; resolved once, before the switch first goes on.
        UClass* WorldClass = nullptr;
#if MELEBRIDGE_FOCUS_TIMER
        int TimerAt = -1;
#else
        int AllowAt = -1;
        DWORD AllowMask = 0;
#endif
        int PausedAt = -1;
        DWORD PausedMask = 0;

#if MELEBRIDGE_FOCUS_TIMER
        constexpr float RaisedTimer = 1e9f;
#endif

        void ResolveWorldFields()
        {
            if (WorldClass != nullptr) return;
            auto const cls = ABioWorldInfo::StaticClass();
            if (cls == nullptr) throw BridgeError("no BioWorldInfo class is loaded");
#if MELEBRIDGE_FOCUS_TIMER
            auto const timer = PropertyOffset(cls, L"m_fNoBrowserWheelTimer");
            if (timer < 0) throw BridgeError("BioWorldInfo has no m_fNoBrowserWheelTimer");
#else
            int allow = -1;
            DWORD allowMask = 0;
            if (!BoolFieldOf(cls, L"m_bAllowBrowserWheel", allow, allowMask)) throw BridgeError("BioWorldInfo has no m_bAllowBrowserWheel");
#endif
            int paused = -1;
            DWORD mask = 0;
            if (!BoolFieldOf(cls, L"m_bPausedByFocusLoss", paused, mask)) throw BridgeError("BioWorldInfo has no m_bPausedByFocusLoss");
#if MELEBRIDGE_FOCUS_TIMER
            TimerAt = timer;
#else
            // Written on every Tick once on: when reflection and the SDK disagree, one of them is wrong, so nothing is written.
            if (allow != Game::FocusFlagsAt || allowMask != Game::AllowBrowserWheelMask || paused != Game::FocusFlagsAt || mask != Game::PausedByFocusLossMask)
                throw BridgeError("BioWorldInfo's focus flags are not where the SDK puts them: keep_ticking stays off");
            AllowAt = allow;
            AllowMask = allowMask;
#endif
            PausedAt = paused;
            PausedMask = mask;
            WorldClass = cls;
        }

#if MELEBRIDGE_FOCUS_TIMER
        float& Timer(AWorldInfo* world) { return *reinterpret_cast<float*>(reinterpret_cast<BYTE*>(world) + TimerAt); }
#else
        DWORD& AllowWord(AWorldInfo* world) { return *reinterpret_cast<DWORD*>(reinterpret_cast<BYTE*>(world) + AllowAt); }
#endif
        DWORD& PausedWord(AWorldInfo* world) { return *reinterpret_cast<DWORD*>(reinterpret_cast<BYTE*>(world) + PausedAt); }

        /// The only place the switch writes bPlayersOnly, and only to end the pause Tick takes on focus loss, the one pause that
        /// sets m_bPausedByFocusLoss. The squad screen, the pause menu, photo mode, Pause Anytime and the F8 editor set bPlayersOnly
        /// on purpose and never that flag, so their pauses stay.
        bool ClearFocusPause(AWorldInfo* world)
        {
            auto& word = PausedWord(world);
            if ((word & PausedMask) == 0) return false;
            word &= ~PausedMask;
            Game::EndFocusPause(world);
            FocusPausesCleared.fetch_add(1, std::memory_order_relaxed);
            return true;
        }

        bool IsWorldTick(UObject* context, UFunction* function)
        {
            static SFXName const TickName{ L"Tick", 0 };
            return WorldClass != nullptr && function->Name == TickName && context->IsA(WorldClass);
        }

#if MELEBRIDGE_FOCUS_TIMER
        /// Before the world's Tick(float fDeltaT): returns whether the timer was raised (then AfterWorldTick puts it back).
        /// fDeltaT is the event's only parameter, at offset 0 of its parameters.
        bool BeforeWorldTick(AWorldInfo* world, void* parms)
        {
            WorldTicks.fetch_add(1, std::memory_order_relaxed);
            ClearFocusPause(world);
            auto& timer = Timer(world);
            auto const delta = parms != nullptr ? *static_cast<float const*>(parms) : 0.f;
            if (timer > (std::max)(delta, 0.f)) return false;
            timer = RaisedTimer;
            TimerRaises.fetch_add(1, std::memory_order_relaxed);
            return true;
        }

        void AfterWorldTick(AWorldInfo* world, bool raised)
        {
            // Still 1e9: ours, back to 0. Anything else was set during the Tick (EndConversation's 1.0): left as it is.
            auto& timer = Timer(world);
            if (raised && timer == RaisedTimer) timer = 0.f;
            // A safety net: a pause the Tick took anyway.
            ClearFocusPause(world);
        }
#else
        /// Before the world's Tick: returns whether m_bAllowBrowserWheel was turned off (then AfterWorldTick turns it back on).
        bool BeforeWorldTick(AWorldInfo* world, void*)
        {
            WorldTicks.fetch_add(1, std::memory_order_relaxed);
            ClearFocusPause(world);
            auto& word = AllowWord(world);
            if ((word & AllowMask) == 0) return false;
            word &= ~AllowMask;
            TimerRaises.fetch_add(1, std::memory_order_relaxed);
            return true;
        }

        void AfterWorldTick(AWorldInfo* world, bool raised)
        {
            if (raised) AllowWord(world) |= AllowMask;
            // A safety net: a pause the Tick took anyway.
            ClearFocusPause(world);
        }
#endif
    }

    bool KeepTickingOn(AWorldInfo* world)
    {
        ResolveWorldFields();
        KeepTicking.store(true, std::memory_order_relaxed);
        return world != nullptr && world->IsA(WorldClass) && ClearFocusPause(world);
    }

    void KeepTickingOff() { KeepTicking.store(false, std::memory_order_relaxed); }

    namespace
    {
        /// This game process's start time, UTC, ISO 8601 to the second; null when Windows does not say. The switch is off at every
        /// game start, so a client that turned it on tells from this whether it still talks to that game.
        json GameStarted()
        {
            static json const started = [] {
                FILETIME created{}, exited{}, kernel{}, user{};
                SYSTEMTIME utc{};
                if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user) || !FileTimeToSystemTime(&created, &utc))
                    return json(nullptr);
                return json(std::format("{:04}-{:02}-{:02}T{:02}:{:02}:{:02}Z", utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond));
            }();
            return started;
        }
    }

    json KeepTickingState()
    {
        return json{
            { "keepTicking", KeepTicking.load(std::memory_order_relaxed) },
            { "worldTicks", WorldTicks.load(std::memory_order_relaxed) },
            { "focusPausesCleared", FocusPausesCleared.load(std::memory_order_relaxed) },
            { "timerRaises", TimerRaises.load(std::memory_order_relaxed) },
            { "gameStarted", GameStarted() },
        };
    }

    bool PausedByFocusLoss(AWorldInfo* world)
    {
        ResolveWorldFields();
        if (!world->IsA(WorldClass)) throw BridgeError(ToUtf8(PathOf(world)) + " is not a BioWorldInfo");
        return (PausedWord(world) & PausedMask) != 0;
    }

    // ! ProcessEvent hook
    // ========================================

    namespace
    {
        using t_UObject_ProcessEvent = void(UObject* Context, UFunction* Function, void* Parms, void* Result);

        /// The loader makes the hook live before InstallHook hands back the original, so a call can arrive while this is still
        /// null (a call through address 0 crashed a boot once, in another ASI): such a call waits for it.
        std::atomic<t_UObject_ProcessEvent*> ProcessEvent_orig{ nullptr };

        void ProcessEvent_hook(UObject* Context, UFunction* Function, void* Parms, void* Result)
        {
            auto orig = ProcessEvent_orig.load(std::memory_order_acquire);
            while (orig == nullptr)
            {
                YieldProcessor();
                orig = ProcessEvent_orig.load(std::memory_order_acquire);
            }

            // The editor's own calls (Canvas.Project, Actor.Trace) only pass through.
            auto const editor = Editor::Busy();
            if (HasJobs.load(std::memory_order_acquire) && !InJobs && !InRaisedTick && !editor)
            {
                InJobsScope scope;
                RunJobs();
            }
            // Calls a request makes (InJobs: a call op's function and what it calls through ProcessEvent before it returns, a console
            // command's, status's reads) and the editor's are not published. status reads the loading movie and the squad screen
            // through calls at every poll of a test's wait, and the editor calls Canvas.Project and Actor.Trace every frame: a watch
            // would show the bridge's own reads as the game's. What a request sets off later (Kismet on the next tick) is published.
            // The subscribe reply says so in "skipped" (docs/formats.md §3.6).
            if (Watching.load(std::memory_order_relaxed) && !InJobs && !editor) PublishGuarded(Context, Function, Parms);

            // keep_ticking wraps the world's own Tick, whoever calls it (jobs and the editor never do).
            AWorldInfo* ticking = nullptr;
            auto raised = false;
            auto const outerRaised = InRaisedTick;
            if (KeepTicking.load(std::memory_order_relaxed) && IsWorldTick(Context, Function))
            {
                ticking = static_cast<AWorldInfo*>(Context);
                raised = BeforeWorldTick(ticking, Parms);
                if (raised) InRaisedTick = true;
            }

            orig(Context, Function, Parms, Result);

            if (ticking != nullptr)
            {
                InRaisedTick = outerRaised;
                AfterWorldTick(ticking, raised);
            }
            if (!InJobs && !editor) Editor::AfterProcessEvent(Context, Function, Parms);
        }
    }

    void InstallHooks(::LESDK::Initializer& Init)
    {
        auto const target = Init.ResolveTyped<t_UObject_ProcessEvent>(BUILTIN_PROCESSEVENT_RVA);
        CHECK_RESOLVED(target);
        auto const orig = static_cast<t_UObject_ProcessEvent*>(Init.InstallHook("UObject::ProcessEvent", target, ProcessEvent_hook));
        CHECK_RESOLVED(orig);
        ProcessEvent_orig.store(orig, std::memory_order_release);
        LEASI_INFO("ProcessEvent hooked");
    }
}
