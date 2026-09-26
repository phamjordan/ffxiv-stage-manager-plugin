using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using StageManager.Core;

namespace StageManager;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string WebOrigin = "";
    public string CastId = "";
    public bool Director;
    public bool GhostModels;
    public bool CueSound;
    public float Alpha = .35f;
}

public sealed class Plugin : IAsyncDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objects;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly ICommandManager commands;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly NativeGhosts ghosts;
    private readonly RehearsalEngine engine = new();
    private readonly ConcurrentQueue<(BridgeMessage Message, TaskCompletionSource<BridgeReply> Reply, int Generation, double Received)> inbox = new();
    private readonly ConcurrentQueue<Action> uiActions = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private LoopbackBridge? bridge;
    private PlayerSnapshot? player;
    private RenderTarget[] targets = [];
    private bool visible = true, armed, disposed;
    private string armedVenue = "", status = "Open your Stage Manager song and pair the browser, or import a choreography file.";
    private string importPath = "", origin = "";
    private int generation;
    private float seekSeconds;
    private double nextUpdate, previousMs = -1;
    private string previousCueKey = "", previousSong = "";
    private bool previouslyPlaying;

    public Plugin(IDalamudPluginInterface pi, IFramework framework, IClientState client, IPlayerState playerState,
        IObjectTable objects, ICondition condition, IGameGui gameGui, ICommandManager commands, IDataManager data, IPluginLog log)
    {
        this.pi = pi; this.framework = framework; this.client = client; this.playerState = playerState;
        this.objects = objects; this.condition = condition; this.gameGui = gameGui; this.commands = commands; this.log = log;
        config = pi.GetPluginConfig() as Configuration ?? new(); origin = config.WebOrigin;
        ghosts = new(objects, data, log);
    }

    public Task LoadAsync(CancellationToken cancellationToken) => framework.RunOnFrameworkThread(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        framework.Update += Update;
        client.TerritoryChanged += ZoneChanged;
        client.Logout += Logout;
        pi.UiBuilder.Draw += Draw;
        pi.UiBuilder.OpenMainUi += Open;
        pi.UiBuilder.OpenConfigUi += Open;
        commands.AddHandler("/stage", new CommandInfo(Command) { HelpMessage = "Open Stage Manager. /stage nextpos previews the next position; /stage stop hides the rehearsal." });
    });

    private void Open() => visible = true;
    private void Command(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "nextpos": uiActions.Enqueue(() => engine.PreviewNext(clock.Elapsed.TotalSeconds)); break;
            case "stop": uiActions.Enqueue(Disarm); break;
            default: visible = !visible; break;
        }
    }

    private void Update(IFramework _)
    {
        if (disposed) return;
        var now = clock.Elapsed.TotalSeconds;
        while (uiActions.TryDequeue(out var action))
        {
            try { action(); } catch (Exception ex) { status = ex.Message; log.Error(ex, "Stage Manager operation failed"); }
        }
        while (inbox.TryDequeue(out var pending))
        {
            if (pending.Generation != generation || now - pending.Received > 2.5) { pending.Reply.TrySetResult(new(false, "State expired; retry", null)); continue; }
            try
            {
                var changedSong = engine.Package?.SongId != pending.Message.Transport?.SongId;
                engine.Receive(pending.Message, now);
                if (changedSong) { ghosts.Clear(); previousMs = -1; }
                pending.Reply.TrySetResult(new(true, "Following browser", player));
            }
            catch (Exception ex) when (ex is ArgumentException or NullReferenceException)
            { pending.Reply.TrySetResult(new(false, ex.Message, player)); }
        }
        if (now < nextUpdate) return;
        nextUpdate = now + .05;
        try
        {
            player = Snapshot();
            if (armed && (player is null || engine.Package?.Venue.Id != armedVenue || engine.Package.Venue.Scope != player.Scope)) Disarm();
            if (!armed || player is null || Blocked()) { targets = []; ghosts.Clear(); previouslyPlaying = false; return; }
            targets = engine.Targets(now, config.CastId, config.Director)
                .Where(t => Vector3.Distance(Position(t), ToVector(player.Position)) < 100).ToArray();
            if (config.GhostModels) ghosts.Tick(targets, config.CastId, now, config.Alpha);
            else if (ghosts.Count > 0) ghosts.Clear();
            SoundCue(now);
        }
        catch (Exception ex)
        {
            armed = false; targets = []; log.Error(ex, "Stage Manager rendering stopped");
            status = "Rendering stopped after an error. Inspect /xllog before re-arming.";
            ghosts.Clear();
        }
    }

    private bool Blocked() => !client.IsLoggedIn || client.IsPvP || client.IsGPosing || condition[ConditionFlag.InCombat] ||
        condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
        condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78];

    private unsafe PlayerSnapshot? Snapshot()
    {
        if (Blocked() || !playerState.IsLoaded || objects.LocalPlayer is not { } local) return null;
        var housing = HousingManager.Instance();
        var scope = new VenueScope(client.TerritoryType, playerState.CurrentWorld.RowId,
            housing == null ? -1 : housing->GetCurrentWard(), housing == null ? -1 : housing->GetCurrentPlot(),
            housing == null ? -1 : housing->GetCurrentRoom(), housing == null ? "0" : housing->GetCurrentIndoorHouseId().Id.ToString(), client.Instance);
        var p = local.Position;
        return new(new(p.X, p.Y, p.Z), local.Rotation, scope, local.Name.TextValue);
    }

    private void SoundCue(double now)
    {
        var ms = engine.Position(now);
        var cue = string.Join('|', targets.Select(t => t.ActionKey));
        var playing = engine.Playing && !engine.IsStale(now);
        if (config.CueSound && playing && previouslyPlaying && previousSong == engine.Package?.SongId &&
            previousMs >= 0 && ms >= previousMs && ms - previousMs < 700 && cue != previousCueKey && cue.Length > 0)
            MessageBeep(0x40);
        previousMs = ms; previousCueKey = cue; previouslyPlaying = playing; previousSong = engine.Package?.SongId ?? "";
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);

    private void Disarm() { armed = false; targets = []; ghosts.Clear(); previousMs = -1; previouslyPlaying = false; }
    private void ZoneChanged(uint territory) { armed = false; player = null; targets = []; ghosts.ForgetOnZoneChange(); generation++; }
    private void Logout(int type, int code) { armed = false; player = null; targets = []; ghosts.ForgetOnZoneChange(); generation++; }

    private Task<BridgeReply> Receive(BridgeMessage message)
    {
        if (disposed || inbox.Count >= 8) return Task.FromResult(new BridgeReply(false, "Plugin is busy; retry", null));
        var reply = new TaskCompletionSource<BridgeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        inbox.Enqueue((message, reply, generation, clock.Elapsed.TotalSeconds));
        return reply.Task;
    }

    private void StartBridge()
    {
        bridge?.Dispose(); bridge = null; generation++;
        bridge = new(origin, Receive, () => Volatile.Read(ref player));
        config.WebOrigin = origin; pi.SavePluginConfig(config);
        status = "Bridge enabled. Copy the pairing token to this PC's browser.";
    }

    private void Draw()
    {
        if (disposed) return;
        DrawMarkers();
        if (!visible) return;
        ImGui.SetNextWindowSize(new Vector2(560, 630), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Stage Manager · Rehearsal###StageManager", ref visible)) { ImGui.End(); return; }
        ImGui.TextWrapped(status);
        if (ImGui.CollapsingHeader("Browser connection", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.InputText("Web origin", ref origin, 256);
            ImGui.TextWrapped("Use the origin shown under In-game rehearsal in your web app. Keep the browser on this Windows PC.");
            if (ImGui.Button(bridge is null ? "Enable local bridge" : "Restart bridge")) uiActions.Enqueue(StartBridge);
            if (bridge is not null)
            {
                ImGui.SameLine();
                if (ImGui.Button("Copy pairing token")) ImGui.SetClipboardText(bridge.Token);
                if (ImGui.Button("Disable bridge")) uiActions.Enqueue(() => { bridge?.Dispose(); bridge = null; generation++; engine.Stop(clock.Elapsed.TotalSeconds); Disarm(); status = "Bridge disabled."; });
                ImGui.TextUnformatted($"Listening on 127.0.0.1:{LoopbackBridge.Port}");
            }
        }
        if (ImGui.CollapsingHeader("Import choreography file"))
        {
            ImGui.InputText("JSON file path", ref importPath, 1024);
            if (ImGui.Button("Load file"))
            {
                var path = importPath.Trim().Trim('"');
                uiActions.Enqueue(() =>
                {
                    if (bridge is not null) throw new ArgumentException("Disable the bridge before switching to file playback.");
                    var file = new FileInfo(path);
                    if (!file.Exists || file.Length > LoopbackBridge.MaxBodyBytes) throw new ArgumentException("Choose a choreography JSON file smaller than 4 MB.");
                    var pkg = WireJson.Parse<RehearsalPackage>(File.ReadAllText(path)); pkg.Validate();
                    Disarm(); engine.Load(pkg, clock.Elapsed.TotalSeconds); status = $"Loaded {pkg.Title}. Select your cast member, then arm the venue.";
                });
            }
        }
        if (engine.Package is { } package)
        {
            ImGui.Separator(); ImGui.TextUnformatted(package.Title);
            var selected = package.Cast.FirstOrDefault(c => c.Id == config.CastId)?.Name ?? "Choose your cast member";
            if (ImGui.BeginCombo("My cast member", selected))
            {
                foreach (var cast in package.Cast)
                    if (ImGui.Selectable($"{cast.Name} ({cast.World})##{cast.Id}", config.CastId == cast.Id))
                    { config.CastId = cast.Id; Save(); uiActions.Enqueue(() => ghosts.Clear()); }
                ImGui.EndCombo();
            }
            if (ImGui.Checkbox("Director view: whole cast (up to 32)", ref config.Director)) Save();
            if (ImGui.Checkbox("Enable ghost models (native prototype)", ref config.GhostModels)) Save();
            if (ImGui.SliderFloat("Ghost opacity", ref config.Alpha, .1f, .8f, "%.2f")) Save();
            if (ImGui.Checkbox("Play in-game cue chime", ref config.CueSound)) Save();
            ImGui.TextWrapped("Ghosts copy nearby characters' game appearance. Missing cast members still have waypoints. Spoken prompts and music remain in the browser.");
            ImGui.TextUnformatted($"Venue: {package.Venue.Name} · Territory {package.Venue.Scope.Territory}");
            ImGui.TextWrapped(player is null ? "Waiting for your character." : package.Venue.Scope == player.Scope ? "Venue matches your current location." : "Different venue: rendering is disabled.");
            if (ImGui.Button(armed ? "Hide rehearsal" : "Arm this venue")) uiActions.Enqueue(() =>
            {
                if (armed) { Disarm(); return; }
                if (player is null || engine.Package?.Venue.Scope != player.Scope) throw new ArgumentException("Go to the calibrated venue first.");
                if (!config.Director && !engine.Package.Cast.Any(c => c.Id == config.CastId)) throw new ArgumentException("Choose your cast member first.");
                armedVenue = engine.Package.Venue.Id; armed = true; status = "Rehearsal armed. /stage stop hides all markers and ghosts.";
            });
            ImGui.SameLine(); if (ImGui.Button("Preview next position")) uiActions.Enqueue(() => engine.PreviewNext(clock.Elapsed.TotalSeconds));
            var now = clock.Elapsed.TotalSeconds;
            ImGui.TextUnformatted($"{engine.Position(now) / 1000:F1}s · {(engine.Playing ? "Playing" : "Paused")} · {(engine.FollowingBrowser ? "Browser timing" : "Local timing")}");
            if (engine.IsStale(now)) ImGui.TextWrapped("Browser updates stopped. Markers are hidden until updates resume.");
            if (bridge is null)
            {
                if (ImGui.Button(engine.Playing ? "Pause" : "Play")) uiActions.Enqueue(() => engine.Seek(engine.Position(clock.Elapsed.TotalSeconds), !engine.Playing, clock.Elapsed.TotalSeconds));
                ImGui.SameLine(); ImGui.InputFloat("Seek (seconds)", ref seekSeconds);
                if (ImGui.Button("Seek")) { var seconds = seekSeconds; uiActions.Enqueue(() => engine.Seek(seconds * 1000, false, clock.Elapsed.TotalSeconds)); }
            }
            ImGui.TextUnformatted($"Visible markers: {targets.Length} · Ghosts: {ghosts.Count}");
            if (ghosts.Status.Length > 0) ImGui.TextWrapped(ghosts.Status);
            foreach (var target in targets)
            {
                var delta = player is null ? Vector3.Zero : Position(target) - ToVector(player.Position);
                ImGui.TextWrapped($"{target.Cast.Name}: {(target.Preview ? "Next" : "Now")} {target.Label} · {MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z):F1} units · height {delta.Y:+0.0;-0.0;0.0} · {(target.Emote.Length > 0 ? "/" + target.Emote : "stand")}");
            }
        }
        ImGui.End();
    }

    private void DrawMarkers()
    {
        var snapshot = player;
        if (!armed || snapshot is null) return;
        var draw = ImGui.GetBackgroundDrawList();
        foreach (var target in targets)
        {
            var pos = Position(target);
            var delta = pos - ToVector(snapshot.Position);
            var distance = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
            var arrived = distance <= .5f && Math.Abs(delta.Y) <= .35f;
            var color = ImGui.ColorConvertFloat4ToU32(arrived ? new Vector4(.35f, 1, .65f, .95f) : target.Preview ? new Vector4(1, .7f, .25f, .95f) : new Vector4(.65f, .55f, 1, .95f));
            for (var i = 0; i < 32; i++)
            {
                var a = MathF.Tau * i / 32; var b = MathF.Tau * (i + 1) / 32;
                if (gameGui.WorldToScreen(pos + new Vector3(MathF.Cos(a) * .45f, .04f, MathF.Sin(a) * .45f), out var p1) &&
                    gameGui.WorldToScreen(pos + new Vector3(MathF.Cos(b) * .45f, .04f, MathF.Sin(b) * .45f), out var p2)) draw.AddLine(p1, p2, color, 2);
            }
            var direction = new Vector3(MathF.Sin(target.Position.Yaw), 0, MathF.Cos(target.Position.Yaw));
            if (gameGui.WorldToScreen(pos, out var foot) && gameGui.WorldToScreen(pos + direction, out var facing)) draw.AddLine(foot, facing, color, 3);
            if (!gameGui.WorldToScreen(pos + new Vector3(0, 2.2f, 0), out var screen)) continue;
            var text = $"{target.Cast.Name} · {(target.Preview ? "NEXT" : "NOW")}\n{(arrived ? "On mark" : $"{distance:F1} units · ΔY {delta.Y:+0.0;-0.0;0.0}")}";
            if (target.MoveInMs > 0) text += $"\nMove in {Math.Ceiling(target.MoveInMs / 1000)}";
            if (target.Emote.Length > 0) text += $"\n/{target.Emote}";
            var size = ImGui.CalcTextSize(text); screen.X -= size.X / 2;
            draw.AddRectFilled(screen - new Vector2(5), screen + size + new Vector2(5), 0xBF16101C, 4);
            draw.AddText(screen, color, text);
        }
    }

    private void Save() => pi.SavePluginConfig(config);
    private static Vector3 Position(RenderTarget t) => new(t.Position.X, t.Position.Y, t.Position.Z);
    private static Vector3 ToVector(WorldPosition p) => new(p.X, p.Y, p.Z);

    public async ValueTask DisposeAsync()
    {
        disposed = true; bridge?.Dispose(); bridge = null;
        await framework.RunOnFrameworkThread(() =>
        {
            framework.Update -= Update; client.TerritoryChanged -= ZoneChanged; client.Logout -= Logout;
            pi.UiBuilder.Draw -= Draw; pi.UiBuilder.OpenMainUi -= Open; pi.UiBuilder.OpenConfigUi -= Open;
            commands.RemoveHandler("/stage"); ghosts.Clear(); targets = [];
            while (inbox.TryDequeue(out var pending)) pending.Reply.TrySetResult(new(false, "Plugin unloaded", null));
        });
    }
}
