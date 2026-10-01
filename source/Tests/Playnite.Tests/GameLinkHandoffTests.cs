using NUnit.Framework;
using Playnite.GameLink;
using Newtonsoft.Json.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;
namespace Playnite.Tests
{
    [TestFixture]
    public class GameLinkHandoffTests
    {
        private sealed class Pending : ILocalHandoff
        {
            public int Calls; public CancellationToken Token; public TaskCompletionSource<bool> Result = new TaskCompletionSource<bool>();
            public Task<bool> SwitchAsync(Guid game, JObject attempt, CancellationToken token) { Calls++; Token = token; return Result.Task; }
        }
        private static JObject Attempt() => new JObject { ["id"] = Guid.NewGuid().ToString(), ["artifactId"] = Guid.NewGuid().ToString(), ["generation"] = "gen",
            ["identity"] = new JObject { ["profileId"] = 1, ["runId"] = "123:seed", ["checkpoint"] = "0:1,2" }, ["expires"] = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds() };
        private static string Event(string session, long seq, string type, JObject attempt) => new JObject { ["version"] = 2,
            ["eventId"] = Guid.NewGuid().ToString(), ["sessionId"] = session, ["gameId"] = "2868840", ["sequence"] = seq,
            ["occurredAtUtc"] = DateTimeOffset.UtcNow.ToString("o"), ["type"] = type, ["payload"] = attempt }.ToString();
        [Test]
        public async Task CandidateCannotLaunchAndDuplicateReadyHasOneEffect()
        {
            var id = Guid.NewGuid().ToString(); var local = new Pending(); var a = Attempt();
            using (var s = new HandoffSession(Guid.NewGuid(), "2868840", id, local)) {
                Assert.IsTrue(s.Receive(Event(id, 1, "handoff.point.reached", a))); Assert.AreEqual(0, local.Calls);
                Assert.IsTrue(s.Receive(Event(id, 3, "handoff.ready", a))); Assert.IsTrue(s.Receive(Event(id, 6, "handoff.ready", a))); Assert.AreEqual(1, local.Calls);
                local.Result.SetResult(true); await Task.Delay(10); Assert.AreEqual(HandoffState.Local, s.State);
            }
        }
        [Test]
        public async Task CancellationDuringSwitchRejectsLateCompletion()
        {
            var id = Guid.NewGuid().ToString(); var local = new Pending(); var a = Attempt();
            using (var s = new HandoffSession(Guid.NewGuid(), "2868840", id, local)) {
                s.Receive(Event(id, 1, "handoff.ready", a)); s.Receive(Event(id, 2, "handoff.cancelled", a)); Assert.IsTrue(local.Token.IsCancellationRequested);
                local.Result.SetResult(true); await Task.Delay(10); Assert.AreEqual(HandoffState.Cancelled, s.State);
            }
        }
        [Test]
        public void AnotherSessionAndLegacyBridgeAreRejected()
        {
            var id = Guid.NewGuid().ToString(); using (var s = new HandoffSession(Guid.NewGuid(), "2868840", id, new Pending())) {
                Assert.IsFalse(s.Receive(Event(Guid.NewGuid().ToString(), 1, "handoff.ready", Attempt())));
                Assert.IsFalse(s.Receive("{\"type\":\"rest_site_reached\"}")); Assert.IsFalse(s.Receive("invalid")); Assert.AreEqual(HandoffState.Playing, s.State);
            }
        }
    }
}
