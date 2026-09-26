using StageManager.Core;

internal static class RehearsalBehaviorChecks
{
    public static void Run(RehearsalPackage package, Action<bool, string> check)
    {
        var engine = new RehearsalEngine();
        var transport = new Transport("cues", 1, package.SongId, 10000, true, true);
        engine.Receive(new(package, transport), 0);
        var playing = engine.Targets(0, "alice", false).Single();
        check(playing.Animate && playing.AnimationKey != "idle", "playing a due emote animates its ghost");
        engine.Receive(new(null, transport with { Sequence = 2, Playing = false }), .1);
        var paused = engine.Targets(.1, "alice", false).Single();
        check(!paused.Animate && paused.AnimationKey == "idle" && paused.RequiredEmote == "beesknees",
            "pause idles the ghost while retaining the actor's cue");
        engine.Receive(new(null, transport with { Sequence = 3 }), .2);
        var resumed = engine.Targets(.2, "alice", false).Single();
        check(resumed.Animate && resumed.AnimationKey != playing.AnimationKey && resumed.AnimationKey != "idle",
            "resume restarts the applicable ghost emote, including the same cue");
        engine.Receive(new(null, transport with { Sequence = 4, PositionMs = 0, Playing = false }), .3);
        var stopped = engine.Targets(.3, "alice", false).Single();
        check(stopped.SlideId == "opening" && stopped.AnimationKey == "idle", "browser stop and rewind returns to an idle opening ghost");

        engine.Load(package, 1);
        engine.Seek(5000, true, 1);
        var preview = engine.Targets(1, "alice", false).Single();
        check(preview.RequiredEmote == "beesknees" && preview.UpcomingEmote is { Emote: "beesknees", InMs: 5000, SlideId: "chorus" },
            "next-position preview counts down the next slide's assigned emote");
        check(preview.AnimationKey == "idle", "preview does not start the next emote early");
        engine.ReturnToCurrent(1);
        check(engine.Targets(1, "alice", false).Single() is { Preview: false, SlideId: "opening" },
            "return to current position cancels the automatic nextpos preview");
        check(!engine.Targets(2, "alice", false).Single().Preview && engine.Position(2) == 6000,
            "preview cancellation survives updates without restarting playback");
        engine.PreviewNext(2);
        check(engine.Targets(2, "alice", false).Single().Preview, "manual preview works again after returning to current");
        engine.ReturnToCurrent(2);
        check(engine.Targets(6, "alice", false).Single() is { Preview: false, SlideId: "chorus" },
            "return override ends at the slide boundary");
        engine.Seek(10000, true, 10);
        var chorus = engine.Targets(10, "alice", false).Single();
        check(chorus.UpcomingEmote is { Emote: "wave", InMs: 1500, SlideId: "chorus" },
            "same-slide lyric emote has a countdown after the slide-start action");
        check(engine.Targets(10, "bob", false).Single().UpcomingEmote is { Emote: "bow", InMs: 1500 },
            "countdowns are filtered for each cast member");
        engine.Seek(11000, false, 20);
        check(engine.Targets(25, "alice", false).Single().UpcomingEmote?.InMs == 500,
            "paused countdown holds its remaining time");
        engine.Seek(11500, true, 30);
        var wave = engine.Targets(30, "alice", false).Single();
        check(wave.Emote == "wave" && wave.UpcomingEmote is null,
            "countdown becomes the due emote at its exact timestamp");
        engine.Seek(1000, false, 40);
        check(engine.Targets(40, "alice", false).Single().UpcomingEmote?.InMs == 9000,
            "rewind restores the upcoming slide's countdown");

        var overrides = WireJson.Parse<RehearsalPackage>(WireJson.Serialize(package));
        overrides.Cues = overrides.Cues.Concat(new[] {
            new Cue("override1", 10000, "emote", ["alice"], "", "wave", 10000),
            new Cue("override2", 10000, "emote", ["alice"], "", "bow", 10000),
            new Cue("offstage", 19000, "emote", ["alice"], "", "wave", 19000),
        }).OrderBy(c => c.AtMs).ToArray();
        engine.Load(overrides, 50);
        engine.Seek(5000, false, 50);
        var next = engine.Targets(50, "alice", false).Single();
        check(next.RequiredEmote == "bow" && next.UpcomingEmote?.Emote == "bow",
            "countdown and readiness use the last simultaneous emote at a slide boundary");
        engine.Seek(12000, true, 51);
        check(engine.Targets(51, "alice", false).Single().UpcomingEmote is null, "offstage emotes do not produce countdowns");

        var tracker = new ReadinessTracker();
        ActorObservation At(RenderTarget target, bool matching = false) => new(new(target.Position.X, target.Position.Y, target.Position.Z), matching);
        var onMark = At(chorus);
        check(tracker.Evaluate(chorus, onMark).State == MarkState.AwaitingEmote, "on the correct mark waits yellow for an emote");
        check(tracker.Evaluate(chorus, At(chorus, true)).State == MarkState.Ready, "the matching emote on the correct mark turns green");
        check(tracker.Evaluate(chorus, onMark).State == MarkState.Ready, "a completed one-shot emote stays green for that cue");
        var away = new ActorObservation(onMark.Position with { X = onMark.Position.X + 3 }, true);
        check(tracker.Evaluate(chorus, away).State == MarkState.OffMark, "the correct emote on the wrong mark is red");
        check(tracker.Evaluate(chorus, onMark).State == MarkState.AwaitingEmote, "returning after leaving the mark requires the emote again");
        check(tracker.Evaluate(chorus, new(onMark.Position with { Y = onMark.Position.Y + 1 }, true)).State == MarkState.OffMark,
            "a different platform height is off mark even with matching XZ");
        var idle = chorus with { RequiredEmote = "", Emote = "", ActionKey = "idle-cue" };
        check(tracker.Evaluate(idle, At(idle)).State == MarkState.Ready, "an on-mark actor with no emote requirement is green");
        tracker.Clear();
        check(tracker.Evaluate(preview, At(preview, true)).State == MarkState.AwaitingEmote,
            "doing the next emote early during preview does not complete the cue");
        check(tracker.Evaluate(paused, At(paused, true)).State == MarkState.AwaitingEmote,
            "paused playback does not credit an emote");
        tracker.Evaluate(chorus, At(chorus, true));
        check(tracker.Evaluate(wave, At(wave)).State == MarkState.AwaitingEmote, "a new same-slide emote resets readiness");
        tracker.Evaluate(chorus, At(chorus, true));
        check(tracker.Evaluate(chorus with { PlaybackEpoch = chorus.PlaybackEpoch + 1 }, At(chorus)).State == MarkState.AwaitingEmote,
            "replaying or seeking clears previously completed cues");
        tracker.Clear();
        tracker.Evaluate(chorus, At(chorus, true));
        var other = chorus with { Cast = package.Cast.Single(c => c.Id == "bob") };
        check(tracker.Evaluate(other, At(other)).State == MarkState.AwaitingEmote,
            "another actor at the same coordinates does not inherit emote completion");
        check(tracker.Evaluate(other, away).State == MarkState.OffMark && tracker.Evaluate(chorus, At(chorus)).State == MarkState.Ready,
            "cast members maintain independent position feedback");
        check(tracker.Evaluate(other, null).State == MarkState.Unavailable, "a missing performer is gray instead of using the local player's position");
        tracker.Retain([]);
        check(tracker.Evaluate(chorus, At(chorus)).State == MarkState.AwaitingEmote, "leaving the slide clears completed readiness");
    }
}
