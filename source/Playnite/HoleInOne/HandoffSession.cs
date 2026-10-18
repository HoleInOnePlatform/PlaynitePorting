using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Playnite.HoleInOne
{
    // One instance per cloud play. UI and store installation remain in the desktop application.
    public sealed class HandoffSession
    {
        private readonly BackendConnection backend;
        private readonly string sessionId;
        private long lastSequence;
        private string runId;
        private string requestId;
        private int polling;
        private bool ticketReturned;

        public HandoffSession(BackendConnection backend, string sessionId)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            BackendConnection.ValidateId(sessionId);
            this.sessionId = sessionId;
        }

        // Caller polls once per second. A ticket is returned only once, after both conditions and ready.
        public async Task<TransferTicket> PollAsync(bool installationComplete, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref polling, 1, 0) != 0)
            {
                return null;
            }

            try
            {
                if (ticketReturned)
                {
                    return null;
                }

                if (requestId == null)
                {
                    var notices = await backend.GetCampfiresAsync(sessionId, lastSequence, cancellationToken);
                    if (notices.Count > 0)
                    {
                        var latest = notices.Last();
                        lastSequence = latest.Sequence;
                        runId = latest.Notice.RunId;
                    }

                    if (!installationComplete || runId == null)
                    {
                        return null;
                    }

                    var requested = await backend.RequestSaveAsync(sessionId, runId, cancellationToken);
                    BackendConnection.ValidateId(requested.RequestId);
                    if (requested.RunId != runId)
                    {
                        throw new InvalidDataException("요청한 판과 세이브 응답이 다름.");
                    }

                    requestId = requested.RequestId;
                }

                var status = await backend.GetSaveAsync(sessionId, requestId, cancellationToken);
                if (status.RequestId != requestId || status.RunId != runId)
                {
                    throw new InvalidDataException("다른 세이브 요청의 응답임.");
                }

                if (status.Status == "failed" || status.Status == "received")
                {
                    throw new InvalidOperationException("세이브 전환을 진행할 수 없음: " + status.Status);
                }

                if (status.Status != "ready")
                {
                    if (status.Status != "pending" && status.Status != "requested" && status.Status != "receiving")
                    {
                        throw new InvalidDataException("알 수 없는 세이브 전송 상태임.");
                    }

                    return null;
                }

                var ticket = status.Ticket;
                if (ticket == null || ticket.SessionId != sessionId || ticket.RequestId != requestId || ticket.RunId != runId)
                {
                    throw new InvalidDataException("전환 정보가 현재 플레이와 일치하지 않음.");
                }

                BackendConnection.ValidateAddress(ticket.BackendUrl);
                cancellationToken.ThrowIfCancellationRequested();
                if (!installationComplete)
                {
                    return null;
                }

                ticketReturned = true;
                return ticket;
            }
            finally
            {
                Volatile.Write(ref polling, 0);
            }
        }

        public static string WriteTicket(string installDirectory, TransferTicket ticket)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            BackendConnection.ValidateAddress(ticket.BackendUrl);
            BackendConnection.ValidateId(ticket.SessionId);
            BackendConnection.ValidateId(ticket.RequestId);
            BackendConnection.ValidateId(ticket.RunId);
            if (!Path.IsPathRooted(installDirectory))
            {
                throw new InvalidDataException("설치된 게임의 절대 경로가 필요함.");
            }

            var directory = Path.Combine(installDirectory, "mods", "GameLoader");
            if (!File.Exists(Path.Combine(directory, "GameLoader.dll")) || !File.Exists(Path.Combine(directory, "GameLoader.json")))
            {
                throw new InvalidOperationException("로컬 게임에 GameLoader를 먼저 설치해야 함.");
            }

            var pending = Path.Combine(directory, "GameLoader.pending.cfg");
            var temporary = pending + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(ticket, Formatting.Indented));
                // Never replace an unconsumed transfer from another play.
                File.Move(temporary, pending);
                return pending;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
