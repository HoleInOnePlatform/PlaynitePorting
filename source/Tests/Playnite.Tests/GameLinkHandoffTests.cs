using NUnit.Framework;
using Playnite.GameLink;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Tests
{
    [TestFixture]
    public class GameLinkHandoffTests
    {
        private sealed class MockHandoff : ILocalHandoff
        {
            public int Calls;
            public bool Result;
            public Task<bool> SwitchAsync(Guid gameId, string handoffId, string artifactId, CancellationToken token)
            {
                Calls++;
                return Task.FromResult(Result);
            }
        }

        private static string Event(string type, string session, string game, long sequence, string eventId = null)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                version = 1, type, eventId = eventId ?? Guid.NewGuid().ToString(),
                sessionId = session, gameId = game, sequence,
                occurredAtUtc = DateTime.UtcNow.ToString("o"),
                payload = new { handoffId = Guid.NewGuid().ToString(), artifactId = "opaque" }
            });
        }

        [TestCase("provider:first")]
        [TestCase("provider:second")]
        public async Task ReadyRunsOneSwitchForAnyGame(string game)
        {
            var sessionId = Guid.NewGuid().ToString();
            var mock = new MockHandoff { Result = true };
            using (var session = new HandoffSession(Guid.NewGuid(), game, sessionId, mock))
            {
                Assert.IsTrue(session.Receive(Event("handoff.point.reached", sessionId, game, 1)));
                Assert.AreEqual(0, mock.Calls);
                var ready = Event("handoff.ready", sessionId, game, 2);
                Assert.IsTrue(session.Receive(ready));
                await Task.Delay(10);
                Assert.AreEqual(1, mock.Calls);
                Assert.AreEqual(HandoffState.Local, session.State);
                Assert.IsFalse(session.Receive(ready));
                Assert.IsFalse(session.Receive(Event("handoff.ready", sessionId, game, 1)));
                Assert.AreEqual(1, mock.Calls);
            }
        }

        [Test]
        public async Task RejectsInvalidEventsAndKeepsCloudOnFailure()
        {
            var sessionId = Guid.NewGuid().ToString();
            var mock = new MockHandoff();
            using (var session = new HandoffSession(Guid.NewGuid(), "provider:game", sessionId, mock))
            {
                Assert.IsFalse(session.Receive("not json"));
                Assert.IsFalse(session.Receive(Event("handoff.ready", Guid.NewGuid().ToString(), "provider:game", 1)));
                Assert.IsFalse(session.Receive(Event("handoff.ready", sessionId, "other", 1)));
                Assert.IsFalse(session.Receive(Event("unknown", sessionId, "provider:game", 1)));
                Assert.IsTrue(session.Receive(Event("handoff.ready", sessionId, "provider:game", 1)));
                await Task.Delay(10);
                Assert.AreEqual(HandoffState.Failed, session.State);
                Assert.AreEqual(1, mock.Calls);
            }
        }
    }
}
