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

    public double Position(double now) => Math.Max(0, anchorMs + (Playing && !IsStale(now) ? (now - anchorClock) * 1000 : 0));
    public bool IsStale(double now) => FollowingBrowser && now - lastPacketClock > 3;

    public void Load(RehearsalPackage package, double now)
    {
        package.Validate();
        Package = package;
        anchorMs = 0; anchorClock = now;
        Playing = false; Active = true; FollowingBrowser = false;
        manualPreview = null; SessionId = null; Sequence = -1;
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
        if (t.SessionId != SessionId || candidate?.SongId != Package?.SongId || Math.Abs(t.PositionMs - Position(now)) > 700)
            manualPreview = null;
        Package = candidate;
        FollowingBrowser = true; Active = t.Active; Playing = t.Playing && t.Active;
        anchorMs = t.PositionMs; anchorClock = lastPacketClock = now;
        SessionId = t.SessionId; Sequence = t.Sequence;
        return true;
    }

    public void Seek(double ms, bool playing, double now)
    {
        if (!RehearsalPackage.ValidTime(ms)) throw new ArgumentException("Invalid playback time.");
        anchorMs = ms; anchorClock = now; Playing = playing; Active = true;
        FollowingBrowser = false; SessionId = null; Sequence = -1; manualPreview = null;
    }

    public void Stop(double now)
    {
        anchorMs = Position(now); anchorClock = now; Playing = false; Active = false; manualPreview = null;
    }

    public void PreviewNext(double now)
    {
        manualPreview = Package?.Slides.FirstOrDefault(s => s.AtMs > Position(now))?.Id;
    }

    public RenderTarget[] Targets(double now, string castId, bool director)
    {
        if (Package is null || !Active || IsStale(now)) return [];
        var ms = Position(now);
        var current = Package.Slides.LastOrDefault(s => s.AtMs <= ms) ?? Package.Slides[0];
        var cast = director ? Package.Cast.Take(32) : Package.Cast.Where(c => c.Id == castId);
        var result = new List<RenderTarget>();
        foreach (var actor in cast)
        {
            var preview = Package.Cues.LastOrDefault(c => c.Kind == "preview" && c.AtMs <= ms && Applies(c, actor.Id) &&
                Package.Slides.Any(s => s.Id == c.SlideId && s.AtMs > ms));
            var previewId = manualPreview ?? preview?.SlideId;
            var targetSlide = Package.Slides.FirstOrDefault(s => s.Id == previewId && s.AtMs > ms) ?? current;
            var position = targetSlide.Positions.FirstOrDefault(p => p.CastId == actor.Id);
            if (position is null) continue; // Actor offstage on this slide.
            var isPreview = targetSlide != current;
            var cue = isPreview ? null : Package.Cues.LastOrDefault(c => c.Kind == "emote" && c.AtMs >= current.AtMs && c.AtMs <= ms && Applies(c, actor.Id));
            var emote = isPreview ? "" : cue?.Emote ?? position.Emote;
            result.Add(new(actor, position, targetSlide.Id, targetSlide.Label, isPreview, emote,
                $"{targetSlide.Id}:{(isPreview ? "preview" : cue?.Id ?? "start")}:{emote}", isPreview && preview is not null ? Math.Max(0, preview.MoveAtMs - ms) : 0));
        }
        return result.ToArray();
    }

    public static bool Applies(Cue cue, string castId) => cue.CastIds.Length == 0 || cue.CastIds.Contains(castId);
}
