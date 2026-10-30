#include "MeleBridge/Bridge.hpp"
#include "MeleBridge/Editor.hpp"
#include "MeleBridge/Reflection.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <future>
#include <vector>

namespace MeleBridge
{
    // ! Jobs
    // ========================================

    namespace
    {
        enum class JobState : int { Queued, Running, Cancelled };

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

        /// An access violation in a request (a stale pointer, a wrong guess about memory) fails the request instead of the game.
        /// No C++ object may live in this frame: __try cannot unwind one.
        bool RunGuarded(void (*work)(void*), void* context)
        {
            __try
            {
                work(context);
                return true;
            }
            __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH)
            {
                return false;
            }
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
                if (!RunGuarded(RunWork, &context))
                {
                    LEASI_ERROR("access violation while running a request");
                    job->Result.set_value(json{ { "ok", false }, { "error", "access violation in the game while running the request" } });
                }
                else if (context.Error)
                {
                    try
                    {
                        std::rethrow_exception(context.Error);
                    }
                    catch (std::exception const& e)
                    {
                        job->Result.set_value(json{ { "ok", false }, { "error", e.what() } });
                    }
                }
                else
                {
                    job->Result.set_value(std::move(context.Result));
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
        // Started just now: let it finish rather than leave it half done.
        return result.get();
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
                    return std::memcmp(&w.Name, &function->Name, sizeof(SFXName)) == 0 && context->IsA(w.Owner);
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

    namespace
    {
        std::atomic<bool> KeepTicking{ false };
        std::atomic<unsigned long long> WorldTicks{ 0 };
        std::atomic<unsigned long long> FocusPausesCleared{ 0 };
        std::atomic<unsigned long long> TimerRaises{ 0 };

        /// BioWorldInfo's fields where the engine laid them out (Editor.cpp says why the SDK's C++ layout is not trusted). Game
        /// thread only; resolved once, before the switch first goes on.
        UClass* WorldClass = nullptr;
        int TimerAt = -1;
        int PausedAt = -1;
        DWORD PausedMask = 0;

        constexpr float RaisedTimer = 1e9f;

        void ResolveWorldFields()
        {
            if (WorldClass != nullptr) return;
            auto const cls = ABioWorldInfo::StaticClass();
            if (cls == nullptr) throw BridgeError("no BioWorldInfo class is loaded");
            auto const timer = PropertyOffset(cls, L"m_fNoBrowserWheelTimer");
            if (timer < 0) throw BridgeError("BioWorldInfo has no m_fNoBrowserWheelTimer");
            int paused = -1;
            DWORD mask = 0;
            if (!BoolFieldOf(cls, L"m_bPausedByFocusLoss", paused, mask)) throw BridgeError("BioWorldInfo has no m_bPausedByFocusLoss");
            TimerAt = timer;
            PausedAt = paused;
            PausedMask = mask;
            WorldClass = cls;
        }

        float& Timer(AWorldInfo* world) { return *reinterpret_cast<float*>(reinterpret_cast<BYTE*>(world) + TimerAt); }
        DWORD& PausedWord(AWorldInfo* world) { return *reinterpret_cast<DWORD*>(reinterpret_cast<BYTE*>(world) + PausedAt); }

        /// The only place the switch writes bPlayersOnly, and only to end the pause Tick takes on focus loss, the one pause that
        /// sets m_bPausedByFocusLoss. The squad screen, the pause menu, photo mode, MelePause and the F8 editor set bPlayersOnly
        /// on purpose and never that flag, so their pauses stay.
        bool ClearFocusPause(AWorldInfo* world)
        {
            auto& word = PausedWord(world);
            if ((word & PausedMask) == 0) return false;
            word &= ~PausedMask;
            world->bPlayersOnly = 0;
            FocusPausesCleared.fetch_add(1, std::memory_order_relaxed);
            return true;
        }

        bool IsWorldTick(UObject* context, UFunction* function)
        {
            static SFXName const TickName{ L"Tick", 0 };
            return WorldClass != nullptr && function->Name == TickName && context->IsA(WorldClass);
        }

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
    }

    bool KeepTickingOn(AWorldInfo* world)
    {
        ResolveWorldFields();
        KeepTicking.store(true, std::memory_order_relaxed);
        return world != nullptr && world->IsA(WorldClass) && ClearFocusPause(world);
    }

    void KeepTickingOff() { KeepTicking.store(false, std::memory_order_relaxed); }

    json KeepTickingState()
    {
        return json{
            { "keepTicking", KeepTicking.load(std::memory_order_relaxed) },
            { "worldTicks", WorldTicks.load(std::memory_order_relaxed) },
            { "focusPausesCleared", FocusPausesCleared.load(std::memory_order_relaxed) },
            { "timerRaises", TimerRaises.load(std::memory_order_relaxed) },
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
                InJobs = true;
                RunJobs();
                InJobs = false;
            }
            if (Watching.load(std::memory_order_relaxed) && !InJobs && !editor) Publish(Context, Function, Parms);

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
