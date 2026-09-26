using System.Net;
using System.Net.Http.Headers;
using System.Text;
using StageManager.Core;

var tests = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); tests++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
var package = WireJson.Parse<RehearsalPackage>(File.ReadAllText(args[0]));
package.Validate();
Check(package.Slides[1].Positions[0].Y == 4.25f, "JavaScript export deserializes with calibrated height");
var fourCorners = WireJson.Parse<RehearsalPackage>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(args[0]) ?? ".", "rehearsal-four-corners.stage.json")));
fourCorners.Validate();
Check(fourCorners.Venue.Boundary is { Length: 4 } boundary && boundary[2] == new WorldPosition(18, 3, 40),
    "four surveyed stage corners survive the browser-to-plugin contract");
Check(fourCorners.Slides[1].Positions[0] is { X: 14, Y: 4.25f, Z: 30 }, "four-corner calibration preserves resolved actor positions");
var badBoundary = WireJson.Parse<RehearsalPackage>(WireJson.Serialize(fourCorners));
badBoundary.Venue = badBoundary.Venue with { Boundary = [new(1, 2, 3)] };
Reject(badBoundary.Validate, "incomplete stage boundary rejected");
badBoundary.Venue = fourCorners.Venue with { Boundary = [new(float.NaN, 3, 20), new(10, 3, 40), new(18, 3, 40), new(18, 3, 20)] };
Reject(badBoundary.Validate, "non-finite boundary coordinates rejected");
badBoundary.Venue = fourCorners.Venue with { Boundary = [new(10, 3, 20), new(10, 5, 40), new(18, 3, 40), new(18, 3, 20)] };
Reject(badBoundary.Validate, "boundary corners on different floor levels rejected");
var engine = new RehearsalEngine(); engine.Load(package, 0);
Check(engine.Targets(0, "alice", false).Single().SlideId == "opening", "first slide appears before playback");
engine.Seek(5000, true, 10);
var preview = engine.Targets(10, "alice", false).Single();
Check(preview.Preview && preview.SlideId == "chorus" && preview.Emote == "" && preview.MoveInMs == 5000, "nextpos shows next mark without playing its action early");
var active = engine.Targets(15, "alice", false).Single();
Check(!active.Preview && active.Emote == "beesknees", "slide boundary starts assigned emote");
Check(engine.Targets(16.5, "alice", false).Single().Emote == "wave", "actor-specific lyric overrides slide emote");
Check(engine.Targets(16.5, "bob", false).Single().Emote == "bow", "separate cast cues remain separate");
Check(engine.Targets(16.5, "alice", true).Length == 2, "director receives whole cast");
engine.Seek(1000, false, 20);
Check(!engine.Targets(99, "alice", false).Single().Preview && engine.Position(99) == 1000, "backward seek clears future pose and pause holds clock");
engine.Seek(18000, false, 30);
Check(engine.Targets(30, "alice", false).Length == 0, "offstage slide clears actors");
var t = new Transport("browser", 1, "song", 4000, true, true);
Check(engine.Receive(new(package, t), 100), "first bridge packet accepted");
Check(Math.Abs(engine.Position(100.25) - 4250) < .01, "playback extrapolates from monotonic receive clock");
Check(!engine.Receive(new(null, t with { PositionMs = 0 }), 101), "out-of-order/replayed packet ignored");
Check(engine.Targets(103.1, "alice", false).Length == 0, "connection timeout hides stale targets");
engine.Receive(new(null, t with { Sequence = 2, PositionMs = 4000, Playing = false }), 104);
Check(engine.Position(106) == 4000, "reconnect restores paused position");
engine.PreviewNext(104);
engine.Receive(new(null, t with { Sequence = 3, PositionMs = 4000, Playing = false }), 104.25);
Check(engine.Targets(104.25, "alice", false).Single().Preview, "manual preview survives normal browser heartbeat");
engine.Receive(new(null, t with { Sequence = 4, Active = false, Playing = false }), 105);
Check(engine.Targets(105, "alice", false).Length == 0, "leaving rehearsal clears targets");
Reject(() => engine.Receive(new(null, t with { SessionId = "new", Sequence = 0 }), 106), "new session needs a package");
Reject(() => engine.Receive(new(null, t with { Sequence = 5, PositionMs = double.NaN }), 106), "non-finite playback rejected");
var bad = WireJson.Parse<RehearsalPackage>(WireJson.Serialize(package)); bad.Slides[1] = bad.Slides[1] with { AtMs = 0 };
Reject(bad.Validate, "duplicate slide times rejected");
bad = WireJson.Parse<RehearsalPackage>(WireJson.Serialize(package)); bad.Slides[0] = bad.Slides[0] with { Positions = [bad.Slides[0].Positions[0] with { X = float.NaN }] };
Reject(bad.Validate, "non-finite native position rejected");

// Regression for ghosts stuck on the opening mark. Verify the transforms sent
// to the native adapter while the browser reuses its original choreography.
// The game's draw-object notifications still require an in-game check.
var movementPackage = WireJson.Parse<RehearsalPackage>(WireJson.Serialize(package));
movementPackage.Cues = []; // Exercise an ordinary slide boundary without /nextpos.
var movement = new RehearsalEngine();
var playback = new Transport("movement", 1, "song", 9750, true, true);
movement.Receive(new(movementPackage, playback), 200);
Check(movement.Targets(200.249, "alice", false).Single().Position == movementPackage.Slides[0].Positions[0],
    "browser playback keeps the opening position until the slide boundary");
var chorusPosition = movementPackage.Slides[1].Positions[0];
Check(movement.Targets(200.25, "alice", false).Single().Position == chorusPosition,
    "slide boundary updates XYZ, height and facing without resending choreography");
Check(movement.Targets(200.25, "alice", true).All(target =>
    target.Position == movementPackage.Slides[1].Positions.Single(p => p.CastId == target.Cast.Id)),
    "director targets all move to the chorus, including the actor with no emote change");
movement.Receive(new(null, playback with { Sequence = 2, PositionMs = 10250 }), 200.5);
var beforeEdit = movement.Targets(200.5, "alice", false).Single();
Check(beforeEdit.Position == chorusPosition, "browser heartbeat retains the new slide position");
var edited = WireJson.Parse<RehearsalPackage>(WireJson.Serialize(movementPackage));
var editedPosition = chorusPosition with { X = 18, Y = 5, Z = 26, Yaw = -.75f };
edited.Slides[1].Positions[0] = editedPosition;
movement.Receive(new(edited, playback with { Sequence = 3, PositionMs = 10500 }), 201);
var afterEdit = movement.Targets(201, "alice", false).Single();
Check(afterEdit.Position == editedPosition && afterEdit.ActionKey == beforeEdit.ActionKey,
    "live position edits update the target even when its action key is unchanged");
movement.Receive(new(null, playback with { Sequence = 4, PositionMs = 1000, Playing = false }), 201.25);
Check(movement.Targets(201.25, "alice", false).Single().Position == edited.Slides[0].Positions[0],
    "browser rewind restores the opening position and facing");
movement.Load(package, 202);
movement.Seek(5000, false, 202);
var previewPosition = movement.Targets(202, "alice", false).Single();
Check(previewPosition.Preview && previewPosition.Position == package.Slides[1].Positions[0],
    "nextpos preview uses the upcoming slide's XYZ and facing before its action starts");

RehearsalBehaviorChecks.Run(package, Check);

if (args.Contains("--bridge"))
{
    var snapshot = new PlayerSnapshot(new(1, 2, 3), .5f, package.Venue.Scope, "Test Actor");
    using var server = new LoopbackBridge("https://stage.test", msg => Task.FromResult(new BridgeReply(true, "OK", snapshot)), () => snapshot);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    async Task<HttpResponseMessage> Send(string path, string origin, string token, string body, HttpMethod? method = null)
    {
        using var req = new HttpRequestMessage(method ?? HttpMethod.Post, $"http://127.0.0.1:{LoopbackBridge.Port}/{path}");
        req.Headers.Add("Origin", origin); req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(req);
    }
    using var good = await Send("snapshot", "https://stage.test", server.Token, "{}");
    var reply = WireJson.Parse<BridgeReply>(await good.Content.ReadAsStringAsync());
    Check(good.StatusCode == HttpStatusCode.OK && reply.Snapshot?.Position.Y == 2, "paired loopback snapshot round trip");
    using var unpaired = await Send("snapshot", "https://stage.test", "wrong", "{}");
    Check(unpaired.StatusCode == HttpStatusCode.Unauthorized, "wrong pairing token rejected");
    using var otherOrigin = await Send("snapshot", "https://other.test", server.Token, "{}");
    Check(otherOrigin.StatusCode == HttpStatusCode.Forbidden, "unpaired website origin rejected");
    using var preflight = await Send("state", "https://stage.test", "none", "{}", HttpMethod.Options);
    Check(preflight.IsSuccessStatusCode && preflight.Headers.GetValues("Access-Control-Allow-Origin").Single() == "https://stage.test", "browser CORS preflight allowed only for configured origin");
    using var invalid = await Send("state", "https://stage.test", server.Token, "{");
    Check(invalid.StatusCode == HttpStatusCode.BadRequest, "malformed JSON rejected");
    using var state = await Send("state", "https://stage.test", server.Token, WireJson.Serialize(new BridgeMessage(package, t)));
    Check(state.IsSuccessStatusCode, "browser choreography packet round trip");
}
Console.WriteLine($"{tests} checks passed.");
