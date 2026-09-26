using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace StageManager.Core;

public record VenueScope(uint Territory, uint World, int Ward, int Plot, int Room, string HouseId, uint Instance);
public record WorldPosition(float X, float Y, float Z);
public record PlayerSnapshot(WorldPosition Position, float Yaw, VenueScope Scope, string Name);
public record Venue(string Id, string Name, VenueScope Scope);
public record CastMember(string Id, string Name, string World, string Role);
public record CastPosition(string CastId, float X, float Y, float Z, float Yaw, string Emote);
public record Slide(string Id, string Label, double AtMs, CastPosition[] Positions);
public record Cue(string Id, double AtMs, string Kind, string[] CastIds, string SlideId, string Emote, double MoveAtMs);
public record Transport(string SessionId, long Sequence, string SongId, double PositionMs, bool Playing, bool Active);
public record BridgeMessage(RehearsalPackage? Package, Transport Transport);
public record BridgeReply(bool Ok, string Message, PlayerSnapshot? Snapshot);
public record EmoteCountdown(string Emote, string SlideId, string Label, double InMs);
public record RenderTarget(CastMember Cast, CastPosition Position, string SlideId, string Label, bool Preview, string Emote, string ActionKey, double MoveInMs,
    string RequiredEmote = "", bool Animate = true, long PlaybackEpoch = 0, EmoteCountdown? UpcomingEmote = null)
{
    public string AnimationKey => Animate && Emote.Length > 0 ? $"{PlaybackEpoch}:{ActionKey}" : "idle";
}

public sealed class RehearsalPackage
{
    public int SchemaVersion { get; set; }
    public string SongId { get; set; } = "";
    public string Title { get; set; } = "";
    public double DurationMs { get; set; }
    public Venue Venue { get; set; } = null!;
    public CastMember[] Cast { get; set; } = [];
    public Slide[] Slides { get; set; } = [];
    public Cue[] Cues { get; set; } = [];

    public void Validate()
    {
        Require(SchemaVersion == 1, "Unsupported choreography version.");
        Require(!string.IsNullOrWhiteSpace(SongId) && SongId.Length <= 128, "Missing song ID.");
        Require(Venue?.Scope is { Territory: > 0, World: > 0 } && !string.IsNullOrEmpty(Venue.Id), "Missing venue calibration.");
        Require(Cast is { Length: > 0 and <= 100 } && Cast.All(c => c is not null && !string.IsNullOrEmpty(c.Id) && !string.IsNullOrEmpty(c.Name)), "Invalid cast.");
        Require(Cast.Select(c => c.Id).Distinct().Count() == Cast.Length, "Duplicate cast IDs.");
        Require(Slides is { Length: > 0 and <= 2000 }, "Expected 1–2000 slides.");
        var castIds = Cast.Select(c => c.Id).ToHashSet();
        double previous = -1;
        var slideIds = new HashSet<string>();
        foreach (var s in Slides)
        {
            Require(s is not null && !string.IsNullOrEmpty(s.Id) && slideIds.Add(s.Id), "Duplicate or missing slide IDs.");
            Require(ValidTime(s.AtMs) && s.AtMs > previous, "Slide times must be distinct and increasing.");
            previous = s.AtMs;
            Require(s.Positions is { Length: <= 100 }, "Invalid slide positions.");
            var seen = new HashSet<string>();
            foreach (var p in s.Positions)
                Require(p is not null && castIds.Contains(p.CastId) && seen.Add(p.CastId) &&
                    Coordinate(p.X) && Coordinate(p.Y) && Coordinate(p.Z) && float.IsFinite(p.Yaw) && Math.Abs(p.Yaw) <= MathF.PI * 2 && ValidEmote(p.Emote), "Invalid actor position or emote.");
        }
        Require(Cues is { Length: <= 10000 }, "Too many cues.");
        var cueIds = new HashSet<string>();
        previous = -1;
        foreach (var cue in Cues)
        {
            Require(cue is not null && !string.IsNullOrEmpty(cue.Id) && cueIds.Add(cue.Id) && ValidTime(cue.AtMs) && cue.AtMs >= previous, "Invalid cue order or ID.");
            previous = cue.AtMs;
            Require(cue.CastIds is not null && cue.CastIds.All(castIds.Contains), "Unknown cue cast member.");
            Require(cue.Kind is "preview" or "emote" && ValidEmote(cue.Emote), "Unknown cue type.");
            if (cue.Kind == "preview")
                Require(Slides.Any(s => s.Id == cue.SlideId && s.AtMs > cue.AtMs) && ValidTime(cue.MoveAtMs) && cue.MoveAtMs >= cue.AtMs, "Invalid next-position cue.");
        }
        Require(ValidTime(DurationMs), "Invalid song duration.");
    }

    public static bool ValidTime(double value) => double.IsFinite(value) && value is >= 0 and <= 86400000;
    private static bool Coordinate(float value) => float.IsFinite(value) && Math.Abs(value) <= 10000;
    private static bool ValidEmote(string? value) => value is not null && value.Length <= 40 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static void Require([DoesNotReturnIf(false)] bool condition, string error) { if (!condition) throw new ArgumentException(error); }
}

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    public static string Serialize<T>(T data) => JsonSerializer.Serialize(data, Options);
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new ArgumentException("Empty JSON.");
}
