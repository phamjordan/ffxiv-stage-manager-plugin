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
