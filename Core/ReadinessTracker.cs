namespace StageManager.Core;

public enum MarkState { Unavailable, OffMark, AwaitingEmote, Ready }
public record ActorObservation(WorldPosition Position, bool MatchingEmote);
public record MarkFeedback(MarkState State, float Distance, float HeightDifference);

// Inputs are read from the assigned real performer, never from a ghost or a
// different cast member. Native object access stays outside this class.
public sealed class ReadinessTracker
{
    private sealed record Progress(string Key, CastPosition Position, bool Completed);
    private readonly Dictionary<string, Progress> progress = [];

    public MarkFeedback Evaluate(RenderTarget target, ActorObservation? actor)
    {
        var key = $"{target.PlaybackEpoch}:{target.ActionKey}:{target.RequiredEmote}";
        if (!progress.TryGetValue(target.Cast.Id, out var previous) || previous.Key != key || previous.Position != target.Position)
            previous = new(key, target.Position, false);
        if (actor is null)
        {
            progress.Remove(target.Cast.Id);
            return new(MarkState.Unavailable, 0, 0);
        }
        var dx = target.Position.X - actor.Position.X;
        var dz = target.Position.Z - actor.Position.Z;
        var dy = target.Position.Y - actor.Position.Y;
        var distance = MathF.Sqrt(dx * dx + dz * dz);
        var onMark = distance <= .5f && Math.Abs(dy) <= .35f;
        // A short /bow stays complete for this cue. Leaving the mark, changing
        // cue, pausing, seeking or replaying starts a fresh readiness check.
        var completed = onMark && (previous.Completed || (target.Animate && !target.Preview && actor.MatchingEmote));
        progress[target.Cast.Id] = previous with { Completed = completed };
        var state = !onMark ? MarkState.OffMark : target.RequiredEmote.Length == 0 || completed ? MarkState.Ready : MarkState.AwaitingEmote;
        return new(state, distance, dy);
    }

    public void Retain(IEnumerable<string> castIds)
    {
        var keep = castIds.ToHashSet();
        foreach (var id in progress.Keys.Where(id => !keep.Contains(id)).ToArray()) progress.Remove(id);
    }

    public void Clear() => progress.Clear();
}
