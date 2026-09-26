namespace StageManager.Core;

// No game pointers, network operations, or wall-clock dependency in the cue engine.
public sealed class RehearsalEngine
{
    public RehearsalPackage? Package { get; private set; }
    public bool FollowingBrowser { get; private set; }
    public bool Active { get; private set; }
    public bool Playing { get; private set; }
    public string? SessionId { get; private set; }
    public long Sequence { get; private set; } = -1;
    private double anchorMs;
    private double anchorClock;
    private double lastPacketClock;
    private string? manualPreview;
    private string? currentPositionOverride;
    private long playbackEpoch;
    private sealed record ScheduledEmote(string Emote, Slide Slide, double AtMs);
    private readonly Dictionary<string, ScheduledEmote[]> emoteSchedules = [];

    public double Position(double now) => Math.Max(0, anchorMs + (Playing && !IsStale(now) ? (now - anchorClock) * 1000 : 0));
    public bool IsStale(double now) => FollowingBrowser && now - lastPacketClock > 3;

    public void Load(RehearsalPackage package, double now)
    {
        package.Validate();
        Package = package;
        BuildEmoteSchedules();
        anchorMs = 0; anchorClock = now;
        Playing = false; Active = true; FollowingBrowser = false;
        manualPreview = null; currentPositionOverride = null; SessionId = null; Sequence = -1; playbackEpoch++;
    }

    public bool Receive(BridgeMessage message, double now)
    {
        var t = message.Transport ?? throw new ArgumentException("Missing playback state.");
        if (string.IsNullOrWhiteSpace(t.SessionId) || t.SessionId.Length > 128 || t.Sequence < 0 || !RehearsalPackage.ValidTime(t.PositionMs))
            throw new ArgumentException("Invalid playback state.");
        if (t.SessionId == SessionId && t.Sequence <= Sequence) return false;
        var candidate = message.Package ?? Package;
        if (t.Active && (candidate is null || candidate.SongId != t.SongId)) throw new ArgumentException("Send choreography before playback.");
        if (message.Package is not null) message.Package.Validate();
        if (t.SessionId != SessionId && t.Active && message.Package is null) throw new ArgumentException("New browser session needs choreography.");
        var discontinuity = t.SessionId != SessionId || candidate?.SongId != Package?.SongId || Math.Abs(t.PositionMs - Position(now)) > 700;
        if (discontinuity) { manualPreview = null; currentPositionOverride = null; }
        if (discontinuity || Playing != (t.Playing && t.Active) || Active != t.Active) playbackEpoch++;
        Package = candidate;
        if (message.Package is not null) BuildEmoteSchedules();
        FollowingBrowser = true; Active = t.Active; Playing = t.Playing && t.Active;
        anchorMs = t.PositionMs; anchorClock = lastPacketClock = now;
        SessionId = t.SessionId; Sequence = t.Sequence;
        return true;
    }

    public void Seek(double ms, bool playing, double now)
    {
        if (!RehearsalPackage.ValidTime(ms)) throw new ArgumentException("Invalid playback time.");
        anchorMs = ms; anchorClock = now; Playing = playing; Active = true;
        FollowingBrowser = false; SessionId = null; Sequence = -1; manualPreview = null; currentPositionOverride = null; playbackEpoch++;
    }

    public void Stop(double now)
    {
        anchorMs = Position(now); anchorClock = now; Playing = false; Active = false;
        manualPreview = null; currentPositionOverride = null; playbackEpoch++;
    }

    public void PreviewNext(double now)
    {
        manualPreview = Package?.Slides.FirstOrDefault(s => s.AtMs > Position(now))?.Id;
        currentPositionOverride = null;
    }

    public void ReturnToCurrent(double now)
    {
        manualPreview = null;
        // Suppress automatic /nextpos too, until the current slide ends.
        currentPositionOverride = Package is null ? null : CurrentSlide(Position(now)).Id;
    }

    private Slide CurrentSlide(double ms) => Package!.Slides.LastOrDefault(s => s.AtMs <= ms) ?? Package.Slides[0];

    private string StartEmote(Slide slide, string castId, CastPosition position) =>
        Package!.Cues.LastOrDefault(c => c.Kind == "emote" && c.AtMs == slide.AtMs && Applies(c, castId))?.Emote ?? position.Emote;

    private void BuildEmoteSchedules()
    {
        emoteSchedules.Clear();
        var slides = Package!.Slides;
        foreach (var actor in Package.Cast)
        {
            var cues = Package.Cues.Where(c => c.Kind == "emote" && Applies(c, actor.Id)).ToArray();
            var events = new List<ScheduledEmote>();
            var cueIndex = 0;
            for (var i = 0; i < slides.Length; i++)
            {
                var slide = slides[i];
                var end = i + 1 < slides.Length ? slides[i + 1].AtMs : double.PositiveInfinity;
                var position = slide.Positions.FirstOrDefault(p => p.CastId == actor.Id);
                while (cueIndex < cues.Length && cues[cueIndex].AtMs < slide.AtMs) cueIndex++;
                var start = position?.Emote ?? "";
                while (cueIndex < cues.Length && cues[cueIndex].AtMs == slide.AtMs) start = cues[cueIndex++].Emote;
                if (position is not null && start.Length > 0) events.Add(new(start, slide, slide.AtMs));
                while (cueIndex < cues.Length && cues[cueIndex].AtMs < end)
                {
                    var cue = cues[cueIndex++];
                    while (cueIndex < cues.Length && cues[cueIndex].AtMs == cue.AtMs) cue = cues[cueIndex++];
                    if (position is not null && cue.Emote.Length > 0) events.Add(new(cue.Emote, slide, cue.AtMs));
                }
            }
            emoteSchedules[actor.Id] = events.ToArray();
        }
    }

    private EmoteCountdown? NextEmote(double ms, string castId, Slide targetSlide, bool preview)
    {
        var next = emoteSchedules.GetValueOrDefault(castId)?.FirstOrDefault(e => e.AtMs > ms && (!preview || e.Slide.AtMs >= targetSlide.AtMs));
        return next is null ? null : new(next.Emote, next.Slide.Id, next.Slide.Label, next.AtMs - ms);
    }

    public RenderTarget[] Targets(double now, string castId, bool director)
    {
        if (Package is null || !Active || IsStale(now)) return [];
        var ms = Position(now);
        var current = CurrentSlide(ms);
        if (currentPositionOverride != current.Id) currentPositionOverride = null;
        var cast = director ? Package.Cast.Take(32) : Package.Cast.Where(c => c.Id == castId);
        var result = new List<RenderTarget>();
        foreach (var actor in cast)
        {
            var preview = Package.Cues.LastOrDefault(c => c.Kind == "preview" && c.AtMs <= ms && Applies(c, actor.Id) &&
                Package.Slides.Any(s => s.Id == c.SlideId && s.AtMs > ms));
            var previewId = currentPositionOverride == current.Id ? null : manualPreview ?? preview?.SlideId;
            var targetSlide = Package.Slides.FirstOrDefault(s => s.Id == previewId && s.AtMs > ms) ?? current;
            var position = targetSlide.Positions.FirstOrDefault(p => p.CastId == actor.Id);
            if (position is null) continue; // Actor offstage on this slide.
            var isPreview = targetSlide != current;
            var cue = isPreview ? null : Package.Cues.LastOrDefault(c => c.Kind == "emote" && c.AtMs >= current.AtMs && c.AtMs <= ms && Applies(c, actor.Id));
            var emote = isPreview ? "" : cue?.Emote ?? position.Emote;
            result.Add(new(actor, position, targetSlide.Id, targetSlide.Label, isPreview, emote,
                $"{targetSlide.Id}:{(isPreview ? "preview" : cue?.Id ?? "start")}:{emote}", isPreview ? Math.Max(0, targetSlide.AtMs - ms) : 0,
                isPreview ? StartEmote(targetSlide, actor.Id, position) : emote, Playing && !isPreview && ms >= targetSlide.AtMs,
                playbackEpoch, NextEmote(ms, actor.Id, targetSlide, isPreview)));
        }
        return result.ToArray();
    }

    public static bool Applies(Cue cue, string castId) => cue.CastIds.Length == 0 || cue.CastIds.Contains(castId);
}
