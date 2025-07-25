// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aidenkrz <aiden@djkraz.com>
// SPDX-FileCopyrightText: 2025 Aviu00 <93730715+Aviu00@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 GoobBot <uristmchands@proton.me>
// SPDX-FileCopyrightText: 2025 Misandry <mary@thughunt.ing>
// SPDX-FileCopyrightText: 2025 Solstice <solsticeofthewinter@gmail.com>
// SPDX-FileCopyrightText: 2025 SolsticeOfTheWinter <solsticeofthewinter@gmail.com>
// SPDX-FileCopyrightText: 2025 gluesniffler <159397573+gluesniffler@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 gluesniffler <linebarrelerenthusiast@gmail.com>
// SPDX-FileCopyrightText: 2025 gus <august.eymann@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Goobstation.Common.CCVar;
using Content.Goobstation.Server.StationEvents.Components;
using Content.Goobstation.Server.StationEvents.Metric;
using Content.Goobstation.Shared.StationEvents;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking.Rules;
using Content.Server.StationEvents;
using Content.Server.StationEvents.Components;
using Content.Shared.Database;
using Content.Shared.GameTicking.Components;
using Content.Shared.Ghost;
using Content.Shared.Humanoid;
using Content.Shared.Random.Helpers;
using Content.Shared.Tag;
using JetBrains.Annotations;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Prometheus;

namespace Content.Goobstation.Server.StationEvents;

/// <summary>
///   Pairs a PossibleEvent with the resultant chaos and a "score" for sorting by the GameDirector
///   Temporary class used in processing and ranking the list of events.
/// </summary>
public sealed class RankedEvent
{
    /// <summary>
    ///   Contains the StationEvent and expected chaos delta
    /// </summary>
    public readonly PossibleEvent PossibleEvent;

    /// <summary>
    ///   Current chaos + PossibleEvent.Chaos at time of creation
    /// </summary>
    public readonly ChaosMetrics Result;

    /// <summary>
    ///   Preference for this RankedEvent, lower is better.
    ///   Essentially the "pain" of how far Result is from the StoryBeat.Goal
    /// </summary>
    public readonly float Score;

    public RankedEvent(PossibleEvent possibleEvent, ChaosMetrics result, float score)
    {
        PossibleEvent = possibleEvent;
        Result = result;
        Score = score;
    }
}
public sealed class PlayerCount
{
    public int Players;
    public int Ghosts;
}

/// <summary>
///   A scheduler which tries to keep station chaos within a set bound over time with the most suitable
///   good or bad events to nudge it in the correct direction.
/// </summary>
[UsedImplicitly]
public sealed class GameDirectorSystem : GameRuleSystem<GameDirectorComponent>
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly EventManagerSystem _event = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ILogManager _log = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly IConfigurationManager _configManager = default!;

    private ISawmill _sawmill = default!;

    private static readonly Gauge EventsRunTotal = Metrics.CreateGauge(
        "game_director_events_run_total",
        "Total number of station events run by the Game Director.",
        "event_name");

    private static readonly Gauge StoryBeatChangesTotal = Metrics.CreateGauge(
        "game_director_story_beat_changes_total",
        "Total number of story beat changes.",
        "story_name", "beat_name");

    private static readonly Gauge ActivePlayers = Metrics.CreateGauge(
        "game_director_active_players",
        "Current number of active players counted by the Game Director.");

    private static readonly Gauge ActiveGhosts = Metrics.CreateGauge(
        "game_director_active_ghosts",
        "Current number of active ghosts counted by the Game Director.");

    private static readonly Gauge RoundstartAntagsSelectedTotal = Metrics.CreateGauge(
        "game_director_roundstart_antags_selected_total",
        "Total number of roundstart antagonists selected.",
        "antag_name");


    public override void Initialize()
    {
        base.Initialize();
        _sawmill = _log.GetSawmill("game_rule");
        SubscribeLocalEvent<GameDirectorComponent, EntityUnpausedEvent>(OnUnpaused);
    }

    private void OnUnpaused(EntityUid uid, GameDirectorComponent component, ref EntityUnpausedEvent args)
    {
        component.BeatStart += args.PausedTime;
        component.TimeNextEvent += args.PausedTime;
    }

    /// <summary>
    /// Removes all labels from a gauge.
    /// </summary>
    /// <typeparam name="TChild"></typeparam>
    /// <param name="gauge"></param>
    public static void ResetGaugeLabels<TChild>(Collector<TChild> gauge) where TChild : ChildBase
    {
        // Get all the label values currently in use
        var labelValues = gauge.GetAllLabelValues().ToList();

        // For each set of label values, remove that shit.
        foreach (var labelSet in labelValues)
            gauge.RemoveLabelled(labelSet);
    }

    /// <summary>
    /// Lists all the label values of a gauge. Useful for debugging!
    /// </summary>
    /// <typeparam name="TChild"></typeparam>
    /// <param name="gauge"></param>
    public static void ListAllLabelValues<TChild>(Collector<TChild> gauge) where TChild : ChildBase
    {
        var labelValues = gauge.GetAllLabelValues();
        foreach (var labelSet in labelValues)
            foreach (var label in labelSet)
                Logger.Warning($"Label: {label}");
    }

    protected override void Added(EntityUid uid, GameDirectorComponent scheduler, GameRuleComponent gameRule, GameRuleAddedEvent args)
    {
        // This deletes all existing metrics and sets them up again.
        ActivePlayers.Set(0);
        ActiveGhosts.Set(0);
        ResetGaugeLabels(EventsRunTotal);
        ResetGaugeLabels(StoryBeatChangesTotal);
        ResetGaugeLabels(RoundstartAntagsSelectedTotal);

        TrySpawnRoundstartAntags(scheduler); // Roundstart antags need to be selected in the lobby
        if(TryComp<SelectedGameRulesComponent>(uid,out var selectedRules))
            SetupEvents(scheduler, CountActivePlayers(), selectedRules);
        else
            SetupEvents(scheduler, CountActivePlayers());
    }

    /// <summary>
    ///   Build a list of events to use for the entire story
    /// </summary>
    private void SetupEvents(GameDirectorComponent scheduler, PlayerCount count, SelectedGameRulesComponent? selectedRules = null)
    {
        scheduler.PossibleEvents.Clear();

        if (selectedRules != null)
            SelectFromTable(scheduler, count, selectedRules);
        else
            SelectFromAllEvents(scheduler, count);

        LogMessage($"All possible events added");
    }

    private void SelectFromAllEvents(GameDirectorComponent scheduler, PlayerCount count)
    {
        foreach (var proto in GameTicker.GetAllGameRulePrototypes())
        {
            if (!proto.TryGetComponent<StationEventComponent>(out var stationEvent, _factory)
            || stationEvent is not { } || !stationEvent.IsSelectable) // dont select inelligable statio events
                continue;

            // Gate here on players, but not on round runtime. The story will probably last long enough for the
            // event to be ready to run again, we'll check CanRun again before we actually launch the event.
            if (!_event.CanRun(proto, stationEvent, count.Players, TimeSpan.MaxValue))
                continue;

            scheduler.PossibleEvents.Add(new PossibleEvent(proto.ID, stationEvent.Chaos));
        }
    }

    private void SelectFromTable(GameDirectorComponent scheduler, PlayerCount count, SelectedGameRulesComponent? selectedRules)
    {
        if (selectedRules == null)
            return;

        if(!_event.TryBuildLimitedEvents(selectedRules.ScheduledGameRules, _event.AvailableEvents(), out var possibleEvents))
            return;

        foreach (var entry in possibleEvents)
        {
            var proto = entry.Key;
            var stationEvent = entry.Value;
            LogMessage(proto.ID);
            scheduler.PossibleEvents.Add(new PossibleEvent(proto.ID, stationEvent.Chaos));
        }
    }


    /// <summary>
    ///   Decide what event to run next
    /// </summary>
    protected override void ActiveTick(EntityUid uid, GameDirectorComponent scheduler, GameRuleComponent gameRule, float frameTime)
    {
        var currTime = _timing.CurTime;
        if (currTime < scheduler.TimeNextEvent)
            return;

        ChaosMetrics chaos = CalculateChaos(uid);
        scheduler.CurrentChaos = chaos;
        LogMessage($"Chaos is: {chaos}");

        if (scheduler.Stories == null || scheduler.Stories.Count() <= 0)
        {
            // No stories (e.g. dummy game rule for printing metrics), end game rule now
            GameTicker.EndGameRule(uid, gameRule);
            return;
        }
        // Decide what story beat to work with (which sets chaos goals)
        var count = CountActivePlayers();
        ActivePlayers.Set(count.Players);
        ActiveGhosts.Set(count.Ghosts);

        var beat = DetermineNextBeat(scheduler, chaos, count);

        // This is the first event, add an automatic delay
        if (scheduler.TimeNextEvent == TimeSpan.Zero)
        {
            var minimumTimeUntilFirstEvent = _configManager.GetCVar(GoobCVars.MinimumTimeUntilFirstEvent);
            scheduler.TimeNextEvent = _timing.CurTime + TimeSpan.FromSeconds(minimumTimeUntilFirstEvent);
            LogMessage($"Started, first event in {minimumTimeUntilFirstEvent} seconds");
            return;
        }

        RankedEvent? chosenEvent = null;
        // Pick the best events (which move the station towards the chaos desired by the beat)
        var bestEvents = ChooseEvents(scheduler, beat, chaos, count);

        // Run the best event here, if we have any to pick from.
        if (bestEvents.Count > 0)
        {
            // Sorts the possible events and then picks semi-randomly.
            // when beat.RandomEventLimit is 1 it's always the "best" event picked. Higher values
            // allow more events to be randomly selected.
            chosenEvent = SelectBest(bestEvents, beat.RandomEventLimit);

            _event.RunNamedEvent(chosenEvent.PossibleEvent.StationEvent);
        }

        if (chosenEvent != null)
        {
            EventsRunTotal.WithLabels(chosenEvent.PossibleEvent.StationEvent).Inc();
            // 2 - 6 minutes until the next event is considered, can vary per beat
            scheduler.TimeNextEvent = currTime + TimeSpan.FromSeconds(_random.NextFloat(beat.EventDelayMin, beat.EventDelayMax));
        }
        else
        {
            // No events were run. Consider again in 30 seconds (current beat or chaos might change)
            LogMessage($"Chaos is: {chaos} (No events ran)", false);
            scheduler.TimeNextEvent = currTime + TimeSpan.FromSeconds(30f);
        }
    }

    /// <summary>
    /// Tries to spawn roundstart antags at the beginning of the round.
    /// </summary>
    private void TrySpawnRoundstartAntags(GameDirectorComponent scheduler)
    {
        if (scheduler.NoRoundstartAntags)
            return;

        // Spawn antags based on GameDirectorComponent
        var weightList = _prototypeManager.Index(scheduler.RoundStartAntagsWeightTable);

#if DEBUG
        var count = _configManager.GetCVar(GoobCVars.GameDirectorDebugPlayerCount);
#else
        var count = GetTotalPlayerCount(_playerManager.Sessions);
#endif
        LogMessage($"Total player count: {count}", false);

        if (!scheduler.DualAntags)
        {
            var pick = weightList.Pick(_random);
            IndexAndStartGameMode(pick);
        }
        else
        {
            var pick = weightList.Pick(_random);
            var weights = weightList.Weights;

            if (_prototypeManager.TryIndex(pick, out IncompatibleGameModesPrototype? incompModes))
                weights = weights.Where(w => !incompModes.Modes.Contains(w.Key)).ToDictionary();

            if (weights.Count == 0)
            {
                IndexAndStartGameMode(pick);
                return;
            }

            var pick2 = _random.Pick(weights);
            var pick1Proto = _prototypeManager.Index(pick);
            var pick2Proto = _prototypeManager.Index(pick2);
            if (!pick2Proto.TryGetComponent<GameRuleComponent>(out var pick2GameRule, _factory) ||
                !pick1Proto.TryGetComponent<GameRuleComponent>(out var pick1GameRule, _factory) ||
                pick1GameRule.MinPlayers > count || pick2GameRule.MinPlayers > count)
            {
                LogMessage("Not enough players for roundstart antags selected...");
                return;
            }
            LogMessage("Choosing roundstart antag");
            LogMessage($"Roundstart antag chosen: {pick}");
            LogMessage($"Roundstart antag chosen: {pick2}");

            RoundstartAntagsSelectedTotal.WithLabels(pick).Inc();
            GameTicker.AddGameRule(pick);
            RoundstartAntagsSelectedTotal.WithLabels(pick2).Inc();
            GameTicker.AddGameRule(pick2);
        }

        return;

        void IndexAndStartGameMode(string pick)
        {
            var pickProto = _prototypeManager.Index(pick);
            if(!pickProto.TryGetComponent<GameRuleComponent>(out var pickGameRule, _factory) ||
               pickGameRule.MinPlayers > count)
            {
                LogMessage("Not enough players for roundstart antags selected...");
                return;
            }
            LogMessage("Choosing roundstart antag");
            LogMessage($"Roundstart antag chosen: {pick}");
            RoundstartAntagsSelectedTotal.WithLabels(pick).Inc();
            GameTicker.AddGameRule(pick);
        }
    }

    /// <summary>
    ///   Count the active players and ghosts on the server.
    ///   Players gates which stories and events are available
    ///   Ghosts can be used to gate certain events (which require ghosts to occur)
    /// </summary>
    private PlayerCount CountActivePlayers()
    {
        var allPlayers = _playerManager.Sessions.ToList();
        var count = new PlayerCount();
        foreach (var player in allPlayers)
        {
            // TODO: A
            if (player.AttachedEntity != null)
            {
                // TODO: Consider a custom component here instead of HumanoidAppearanceComponent to represent
                //        "significant enough to count as a whole player"
                if (HasComp<HumanoidAppearanceComponent>(player.AttachedEntity))
                    count.Players += 1;
                else if (HasComp<GhostComponent>(player.AttachedEntity))
                    count.Ghosts += 1;
            }
        }

        return count;
    }

    /// <summary>
    ///   Count all the players on the server.
    /// </summary>
    public int GetTotalPlayerCount(IList<ICommonSession> pool)
    {
        var count = 0;
        foreach (var session in pool)
        {
            if (session.Status is SessionStatus.Disconnected or SessionStatus.Zombie)
                continue;

            count++;
        }

        return count;
    }

    /// <summary>
    ///   Sorts the possible events and then picks semi-randomly.
    ///   when maxRandom is 1 it's always the "best" event picked. Higher values allow more events to be randomly selected.
    /// </summary>
    protected RankedEvent SelectBest(List<RankedEvent> bestEvents, int maxRandom)
    {
        var ranked = bestEvents.OrderBy(ev => ev.Score).Take(maxRandom).ToList();

        var rand = _random.NextFloat();
        rand *= rand; // Square it, which leads to a front-weighted distribution
                      // Of 3 items, there is (50% chance of 1, 36% chance of 2 and 14% chance of 3)
        rand *= ranked.Count - 1;

        var rankedEvent = ranked[(int) Math.Round(rand)];

        // Pick this event
        var events = String.Join(", ", ranked.Select(r => r.PossibleEvent.StationEvent));
        LogMessage($"Picked {rankedEvent.PossibleEvent.StationEvent} from best events (in sequence) {events}");
        return rankedEvent;
    }

    private void LogMessage(string message, bool showChat=true)
    {
        // TODO: LogMessage strings all require localization.
        _adminLogger.Add(LogType.GameDirector, showChat?LogImpact.Medium:LogImpact.High, $"{message}");
        if (showChat)
            _chat.SendAdminAnnouncement("GameDirector " + message);

    }
    /// <summary>
    ///   Returns the StoryBeat that should be currently used to select events.
    ///   Advances the current story and picks new stories when the current beat is complete.
    /// </summary>
    private StoryBeatPrototype DetermineNextBeat(GameDirectorComponent scheduler, ChaosMetrics chaos, PlayerCount count)
    {
        var curTime = _timing.CurTime;
        // Potentially Complete CurrBeat, which is always scheduler.CurrStory[0]
        if (scheduler.RemainingBeats.Count > 0)
        {
            var beatName = scheduler.RemainingBeats[0];
            var beat = _prototypeManager.Index<StoryBeatPrototype>(beatName);
            var secsInBeat = (curTime - scheduler.BeatStart).TotalSeconds;

            if (secsInBeat > beat.MaxSecs)
            {
                // Done with this beat (it has lasted too long)
                _sawmill.Info($"StoryBeat {beatName} complete. It's lasted {scheduler.BeatStart} out of a maximum of {beat.MaxSecs} seconds.");
            }
            else if (secsInBeat > beat.MinSecs)
            {
                // Determine if we meet the chaos thresholds to exit this beat
                if (!beat.EndIfAnyWorse.Empty && chaos.AnyWorseThan(beat.EndIfAnyWorse))
                {
                    // Done with this beat (chaos exceeded set bad level)
                    _sawmill.Info($"StoryBeat {beatName} complete. Chaos exceeds {beat.EndIfAnyWorse} (EndIfAnyWorse).");
                }
                else if(!beat.EndIfAllBetter.Empty && chaos.AllBetterThan(beat.EndIfAllBetter))
                {
                    // Done with this beat (chaos reached set good level)
                    _sawmill.Info($"StoryBeat {beatName} complete. Chaos better than {beat.EndIfAllBetter} (EndIfAllBetter).");
                }
                else
                {
                    return beat;
                }
            }
            else
            {
                return beat;
            }

            // If we didn't return by here, we are done with this beat.
            //   While RemoveAt(0) does a O(n) shift, we're shifting string pointers and usually n < 20.
            scheduler.RemainingBeats.RemoveAt(0);
        }
        scheduler.BeatStart = curTime;

        // Advance in the current story
        if (scheduler.RemainingBeats.Count > 0)
        {
            // Return the next beat in the current story.
            var beatName = scheduler.RemainingBeats[0];
            var beat = _prototypeManager.Index<StoryBeatPrototype>(beatName);

            StoryBeatChangesTotal.WithLabels(scheduler.CurrentStoryName.ToString() ?? "Unknown", beatName).Inc();
            LogMessage($"New StoryBeat {beatName}: {beat.Description}. Goal is {beat.Goal}");
            return beat;
        }

        // Need to find a new story. Pick a random one which meets our needs.
        if (scheduler.Stories != null)
        {
            var stories = scheduler.Stories.ToList();
            _random.Shuffle(stories);

            foreach (var storyName in stories)
            {
                var story = _prototypeManager.Index<StoryPrototype>(storyName);
                if (story.MinPlayers > count.Players || story.MaxPlayers < count.Players || story.Beats == null)
                    continue;


                // A new story was picked. Copy the full list of beats (for us to pop beats from the front as we proceed)
                foreach (var storyBeat in story.Beats)
                {
                    scheduler.RemainingBeats.Add(storyBeat);
                }


                scheduler.CurrentStoryName = storyName;
                SetupEvents(scheduler, count);
                _sawmill.Info(
                    $"New Story {storyName}: {story.Description}. {scheduler.PossibleEvents.Count} events to use.");

                var beatName = scheduler.RemainingBeats[0];
                var beat = _prototypeManager.Index<StoryBeatPrototype>(beatName);

                StoryBeatChangesTotal.WithLabels(storyName.ToString() ?? "Unknown", beatName).Inc();
                LogMessage($"First StoryBeat {beatName}: {beat.Description}. Goal is {beat.Goal}");
                return beat;
            }
        }

        // Just use the fallback beat when no stories were found. That beat does exist, right!?
        scheduler.RemainingBeats.Add(scheduler.FallbackBeatName);
        return _prototypeManager.Index<StoryBeatPrototype>(scheduler.FallbackBeatName);
    }

    private float RankChaosDelta(ChaosMetrics chaos)
    {
        // Just a sum of squares (trying to get close to 0 on every score)
        //   Lower is better
        // Note:  if the chaos value is above 655.36 then its square is above maxint (inside FixedPoint2) and it wraps
        //        around. We need a full float range to handle the square.
        return chaos.ChaosDict.Values.Sum(v => (float)(v) * (float)(v));
    }

    private List<RankedEvent> ChooseEvents(GameDirectorComponent scheduler, StoryBeatPrototype beat, ChaosMetrics chaos, PlayerCount count)
    {
        // TODO : Potentially filter Chaos here using CriticalLevels & DangerLevels which force us to focus on
        //        big problems (lots of hostiles, spacing) prior to smaller ones (food & drink)
        var desiredChange = beat.Goal.ExclusiveSubtract(chaos);
        var result = FilterAndScore(scheduler, chaos, desiredChange, count);

        if (result.Count > 0)
            return result;


        // Fall back to improving all scores (not just the ones the beat is focused on)
        //   Generally this means reducing chaos (unspecified scores are desired to be 0).
        var allDesiredChange = beat.Goal - chaos;
        result = FilterAndScore(scheduler, chaos, allDesiredChange, count, inclNoChaos:true);

        return result;
    }

    /// <summary>
    ///   Filter only to events which improve the chaos score in alignment with desiredChange.
    ///   Score them (lower is better) in how well they do this.
    /// </summary>
    private List<RankedEvent> FilterAndScore(GameDirectorComponent scheduler, ChaosMetrics chaos,
        ChaosMetrics desiredChange, PlayerCount count, bool inclNoChaos = false)
    {
        var noEvent = RankChaosDelta(desiredChange);
        var result = new List<RankedEvent>();

        // Choose an event that specifically achieves chaos goals, focusing only on them.
        foreach (var possibleEvent in scheduler.PossibleEvents)
        {
            // How much of the relevant chaos will be left after this event has occurred
            var relevantChaosDelta = desiredChange.ExclusiveSubtract(possibleEvent.Chaos);
            var rank = RankChaosDelta(relevantChaosDelta);

            var allChaosAfter = chaos + possibleEvent.Chaos;

            // Some events have no chaos score assigned. Treat them as if they change nothing and mix them in for flavor.
            var noChaosEvent = inclNoChaos && possibleEvent.Chaos.Empty;

            if (rank < noEvent || noChaosEvent)
            {
                // Look up this event's prototype and check it is ready to run.
                var proto = _prototypeManager.Index<EntityPrototype>(possibleEvent.StationEvent);

                if (!proto.TryGetComponent<StationEventComponent>(out var stationEvent, _factory))
                    continue;

                if (!_event.CanRun(proto, stationEvent, count.Players, GameTicker.RoundDuration()))
                    continue;

                result.Add(new RankedEvent(possibleEvent, allChaosAfter, rank));
            }
        }

        return result;
    }

    public ChaosMetrics CalculateChaos(EntityUid uid)
    {
        // Send an event to chaos metric components on the Game Director's entity.
        var calcEvent = new CalculateChaosEvent(new ChaosMetrics());
        RaiseLocalEvent(uid, ref calcEvent);

        var metrics = calcEvent.Metrics;

        // Calculated metrics
        metrics.ChaosDict[ChaosMetric.Combat] = metrics.ChaosDict.GetValueOrDefault(ChaosMetric.Friend) +
                                                metrics.ChaosDict.GetValueOrDefault(ChaosMetric.Hostile);
        return calcEvent.Metrics;
    }
}
