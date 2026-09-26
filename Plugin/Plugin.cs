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
    public bool ShowCountdown = true;
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
    private readonly ReadinessTracker readiness = new();
    private readonly Dictionary<string, MarkFeedback> feedback = [];
    private readonly ConcurrentQueue<(BridgeMessage Message, TaskCompletionSource<BridgeReply> Reply, int Generation, double Received)> inbox = new();
    private readonly ConcurrentQueue<Action> uiActions = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private LoopbackBridge? bridge;
    private PlayerSnapshot? player;
    private RenderTarget[] targets = [];
    private bool visible = true, armed, disposed;
    private string armedVenue = "", status = "Connect your Stage Manager browser to begin.";
    private string origin = "";
    private int generation;
    private double nextUpdate, previousMs = -1;
    private string previousCueKey = "", previousSong = "";
    private bool previouslyPlaying;

    public Plugin(IDalamudPluginInterface pi, IFramework framework, IClientState client, IPlayerState playerState,
        IObjectTable objects, ICondition condition, IGameGui gameGui, ICommandManager commands, IDataManager data, IPluginLog log)
    {
        this.pi = pi; this.framework = framework; this.client = client; this.playerState = playerState;
        this.objects = objects; this.condition = condition; this.gameGui = gameGui; this.commands = commands; this.log = log;
        config = pi.GetPluginConfig() as Configuration ?? new(); origin = string.IsNullOrWhiteSpace(config.WebOrigin) ? "https://ffxiv.jjammin.com" : config.WebOrigin;
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
        commands.AddHandler("/stage", new CommandInfo(Command) { HelpMessage = "Open Stage Manager. /stage nextpos previews the next position; /stage current returns to the current mark; /stage stop hides the rehearsal." });
    });

    private void Open() => visible = true;
    private void Command(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "nextpos": uiActions.Enqueue(() => engine.PreviewNext(clock.Elapsed.TotalSeconds)); break;
            case "current": uiActions.Enqueue(() => engine.ReturnToCurrent(clock.Elapsed.TotalSeconds)); break;
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
                if (changedSong) { ghosts.Clear(); readiness.Clear(); feedback.Clear(); previousMs = -1; }
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
            if (!armed || player is null || Blocked()) { targets = []; ghosts.Clear(); readiness.Clear(); feedback.Clear(); previouslyPlaying = false; return; }
            targets = engine.Targets(now, config.CastId, config.Director)
                .Where(t => Vector3.Distance(Position(t), ToVector(player.Position)) < 100).ToArray();
            readiness.Retain(targets.Select(t => t.Cast.Id));
            feedback.Clear();
            foreach (var target in targets) feedback[target.Cast.Id] = readiness.Evaluate(target, ghosts.Observe(target, config.CastId));
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

    private void Disarm() { armed = false; targets = []; ghosts.Clear(); readiness.Clear(); feedback.Clear(); previousMs = -1; previouslyPlaying = false; }
    private void ZoneChanged(uint territory) { armed = false; player = null; targets = []; ghosts.ForgetOnZoneChange(); readiness.Clear(); feedback.Clear(); generation++; }
    private void Logout(int type, int code) { armed = false; player = null; targets = []; ghosts.ForgetOnZoneChange(); readiness.Clear(); feedback.Clear(); generation++; }

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
        engine.Stop(clock.Elapsed.TotalSeconds); Disarm();
        bridge = new(origin, Receive, () => Volatile.Read(ref player));
        config.WebOrigin = origin; pi.SavePluginConfig(config);
        status = "Bridge enabled. Copy the pairing token to this PC's browser.";
    }

    private void Draw()
    {
        if (disposed) return;
        DrawMarkers();
        DrawCountdown();
        if (!visible) return;
        ImGui.SetNextWindowSize(new Vector2(590, 570), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Stage Manager · Rehearsal###StageManager", ref visible)) { ImGui.End(); return; }
        var now = clock.Elapsed.TotalSeconds;
        ImGui.TextUnformatted(engine.Package?.Title ?? "Stage Manager");
        var connection = bridge is null ? "Disconnected" : !engine.FollowingBrowser ? "Waiting for browser" :
            engine.IsStale(now) ? "Browser connection lost" : !engine.Active ? "Waiting for a song" : engine.Playing ? "Playing" : "Paused / stopped";
        ImGui.TextUnformatted($"{connection} · {engine.Position(now) / 1000:F1}s · {(armed ? "Rehearsal visible" : "Rehearsal hidden")}");
        ImGui.Separator();
        if (ImGui.BeginTabBar("StageManagerTabs"))
        {
            if (ImGui.BeginTabItem("Rehearsal")) { DrawRehearsal(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Connection")) { DrawConnection(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Display & sound")) { DrawDisplay(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.Separator();
        ImGui.TextWrapped(status);
        ImGui.End();
    }

    private void DrawConnection()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("Open Stage Manager in a browser on this PC, then open In-game rehearsal.");
        ImGui.InputText("Web origin", ref origin, 256);
        ImGui.TextWrapped("Use the Web origin shown by the website, without a page path.");
        if (bridge is null)
        {
            if (ImGui.Button("Enable local bridge")) uiActions.Enqueue(StartBridge);
        }
        else
        {
            if (ImGui.Button("Copy pairing token")) ImGui.SetClipboardText(bridge.Token);
            ImGui.TextWrapped("Paste the token into the website and click Connect. Song timing and choreography sync automatically.");
            if (ImGui.CollapsingHeader("Connection controls"))
            {
                if (ImGui.Button("Restart bridge")) uiActions.Enqueue(StartBridge);
                ImGui.SameLine();
                if (ImGui.Button("Disconnect")) uiActions.Enqueue(() =>
                {
                    bridge?.Dispose(); bridge = null; generation++; engine.Stop(clock.Elapsed.TotalSeconds);
                    Disarm(); status = "Disconnected. Enable the bridge to pair again.";
                });
                ImGui.TextWrapped("Restart after changing the origin. Restarting creates a new pairing token.");
            }
        }
    }

    private void DrawRehearsal()
    {
        if (engine.Package is not { } package)
        {
            ImGui.TextWrapped("Start in the Connection tab. After pairing, open a calibrated song in the website; it will appear here automatically.");
            return;
        }
        ImGui.Spacing(); ImGui.TextUnformatted("Performer");
        var selected = package.Cast.FirstOrDefault(c => c.Id == config.CastId)?.Name ?? "Choose your cast member";
        if (ImGui.BeginCombo("My cast member", selected))
        {
            foreach (var cast in package.Cast)
                if (ImGui.Selectable($"{cast.Name} ({cast.World})##{cast.Id}", config.CastId == cast.Id))
                {
                    config.CastId = cast.Id; Save();
                    uiActions.Enqueue(() => { ghosts.Clear(); readiness.Clear(); feedback.Clear(); });
                }
            ImGui.EndCombo();
        }
        if (ImGui.Checkbox("Director view: show the whole cast", ref config.Director)) { Save(); uiActions.Enqueue(() => readiness.Clear()); }
        ImGui.TextWrapped("Each ring checks its assigned performer. Select your own role above.");
        ImGui.Separator(); ImGui.TextUnformatted($"Venue · {package.Venue.Name}");
        ImGui.TextWrapped(player is null ? "Waiting for your character." : package.Venue.Scope == player.Scope ?
            "Your location matches this calibration." : "Travel to the calibrated venue before showing the rehearsal.");
        if (ImGui.Button(armed ? "Hide rehearsal" : "Arm this venue")) uiActions.Enqueue(() =>
        {
            if (armed) { Disarm(); return; }
            if (bridge is null || !engine.Active || engine.IsStale(clock.Elapsed.TotalSeconds)) throw new ArgumentException("Connect the browser and open your song first.");
            if (player is null || engine.Package?.Venue.Scope != player.Scope) throw new ArgumentException("Go to the calibrated venue first.");
            if (!config.Director && !engine.Package.Cast.Any(c => c.Id == config.CastId)) throw new ArgumentException("Choose your cast member first.");
            armedVenue = engine.Package.Venue.Id; armed = true; status = "Rehearsal visible. Use the browser to play, pause or stop.";
        });
        ImGui.Spacing(); ImGui.TextUnformatted("Position preview");
        ImGui.BeginDisabled(!armed);
        if (ImGui.Button("Preview next position")) uiActions.Enqueue(() => engine.PreviewNext(clock.Elapsed.TotalSeconds));
        ImGui.SameLine();
        if (ImGui.Button("Return to current position")) uiActions.Enqueue(() => engine.ReturnToCurrent(clock.Elapsed.TotalSeconds));
        ImGui.EndDisabled();
        ImGui.TextWrapped("Return cancels the preview until this slide ends. Playback continues normally.");
        ImGui.Separator(); ImGui.TextUnformatted("Cast readiness");
        ImGui.TextWrapped("Red: off mark · Yellow: emote pending · Green: ready · Gray: performer not nearby");
        foreach (var target in targets)
        {
            var mark = Feedback(target);
            ImGui.TextColored(MarkColor(mark.State), $"{target.Cast.Name} · {(target.Preview ? "Next" : "Now")} {target.Label}");
            ImGui.TextWrapped(FeedbackText(target, mark));
            if (target.UpcomingEmote is { } upcoming) ImGui.TextWrapped(CountdownText(upcoming));
        }
        if (targets.Length == 0) ImGui.TextWrapped(armed ? "No onstage positions to show at this time." : "Arm the venue to see readiness.");
        if (ghosts.Status.Length > 0) ImGui.TextWrapped(ghosts.Status);
    }

    private void DrawDisplay()
    {
        ImGui.Spacing(); ImGui.TextUnformatted("In-game visuals");
        if (ImGui.Checkbox("Show ghost models", ref config.GhostModels)) Save();
        ImGui.BeginDisabled(!config.GhostModels);
        if (ImGui.SliderFloat("Ghost opacity", ref config.Alpha, .1f, .8f, "%.2f")) Save();
        ImGui.EndDisabled();
        if (ImGui.Checkbox("Show my emote countdown HUD", ref config.ShowCountdown)) Save();
        ImGui.TextWrapped("Countdowns also appear above each cast marker. Ghosts stand idle while the browser is paused or stopped.");
        ImGui.Separator(); ImGui.TextUnformatted("Audio");
        if (ImGui.Checkbox("Play in-game cue chime", ref config.CueSound)) Save();
        ImGui.TextWrapped("Music and spoken prompts come from your browser.");
        ImGui.Separator(); ImGui.TextUnformatted("Ring colors");
        ImGui.TextColored(MarkColor(MarkState.OffMark), "Red · Assigned performer is off their mark");
        ImGui.TextColored(MarkColor(MarkState.AwaitingEmote), "Yellow · On mark, waiting for the required emote");
        ImGui.TextColored(MarkColor(MarkState.Ready), "Green · On mark and cue complete, or no emote required");
        ImGui.TextColored(MarkColor(MarkState.Unavailable), "Gray · Assigned performer is not loaded nearby");
        ImGui.TextWrapped("An emote counts when observed on the correct mark after its cue is due. Short emotes stay complete until the next cue, leaving the mark, or restarting playback.");
    }

    private MarkFeedback Feedback(RenderTarget target) => feedback.GetValueOrDefault(target.Cast.Id) ?? new(MarkState.Unavailable, 0, 0);
    private static Vector4 MarkColor(MarkState state) => state switch
    {
        MarkState.OffMark => new(1, .3f, .3f, .95f),
        MarkState.AwaitingEmote => new(1, .85f, .2f, .95f),
        MarkState.Ready => new(.35f, 1, .65f, .95f),
        _ => new(.65f, .65f, .7f, .95f),
    };
    private string FeedbackText(RenderTarget target, MarkFeedback mark) => mark.State switch
    {
        MarkState.Unavailable => "Performer not nearby",
        MarkState.OffMark => $"Off mark · {mark.Distance:F1} units · height {mark.HeightDifference:+0.0;-0.0;0.0}",
        MarkState.AwaitingEmote => target.Preview ? $"On mark · wait for /{target.RequiredEmote}" :
            !engine.Playing ? $"On mark · paused · /{target.RequiredEmote} pending" : $"On mark · do /{target.RequiredEmote}",
        _ => target.RequiredEmote.Length > 0 ? "On mark · emote complete" : "On mark · ready",
    };
    private string CountdownText(EmoteCountdown cue) => $"/{cue.Emote} in {Math.Ceiling(cue.InMs / 1000):0}s · {cue.Label}{(!engine.Playing ? " · paused" : "")}";

    private void DrawCountdown()
    {
        if (!armed || !config.ShowCountdown) return;
        var target = targets.FirstOrDefault(t => t.Cast.Id == config.CastId);
        if (target is null) return;
        var mark = Feedback(target);
        var due = !target.Preview && target.RequiredEmote.Length > 0 && mark.State != MarkState.Ready;
        if (target.UpcomingEmote is null && !due) return;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(viewport.Pos.X + viewport.Size.X / 2, viewport.Pos.Y + 80), ImGuiCond.Always, new Vector2(.5f, 0));
        ImGui.SetNextWindowBgAlpha(.85f);
        if (ImGui.Begin("Stage Manager cues###StageManagerCountdown", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.SetWindowFontScale(1.2f);
            ImGui.TextUnformatted(target.Cast.Name);
            if (due) ImGui.TextColored(MarkColor(mark.State), engine.Playing ? $"NOW · /{target.RequiredEmote}" : $"PAUSED · /{target.RequiredEmote}");
            if (target.UpcomingEmote is { } cue) ImGui.TextColored(new Vector4(1, .85f, .2f, 1), CountdownText(cue));
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
            var mark = Feedback(target);
            var color = ImGui.ColorConvertFloat4ToU32(MarkColor(mark.State));
            for (var i = 0; i < 32; i++)
            {
                var a = MathF.Tau * i / 32; var b = MathF.Tau * (i + 1) / 32;
                if (gameGui.WorldToScreen(pos + new Vector3(MathF.Cos(a) * .45f, .04f, MathF.Sin(a) * .45f), out var p1) &&
                    gameGui.WorldToScreen(pos + new Vector3(MathF.Cos(b) * .45f, .04f, MathF.Sin(b) * .45f), out var p2)) draw.AddLine(p1, p2, color, 2);
            }
            var direction = new Vector3(MathF.Sin(target.Position.Yaw), 0, MathF.Cos(target.Position.Yaw));
            if (gameGui.WorldToScreen(pos, out var foot) && gameGui.WorldToScreen(pos + direction, out var facing)) draw.AddLine(foot, facing, color, 3);
            if (!gameGui.WorldToScreen(pos + new Vector3(0, 2.2f, 0), out var screen)) continue;
            var text = $"{target.Cast.Name} · {(target.Preview ? "NEXT" : "NOW")}\n{FeedbackText(target, mark)}";
            if (target.MoveInMs > 0) text += $"\nSlide in {Math.Ceiling(target.MoveInMs / 1000)}s{(!engine.Playing ? " · paused" : "")}";
            if (target.UpcomingEmote is { } cue) text += $"\n{CountdownText(cue)}";
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
