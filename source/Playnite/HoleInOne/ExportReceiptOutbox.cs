using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.HoleInOne
{
    // Credentials never go on disk. Scope pending cleanup to the original service/account.
    public sealed class ExportReceiptOutbox
    {
        public sealed class Receipt
        {
            public string SessionId { get; set; }
            public string RequestId { get; set; }
            public string Sha256 { get; set; }
        }
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new ConcurrentDictionary<string, SemaphoreSlim>();
        private readonly string directory;
        private readonly SemaphoreSlim gate;

        public ExportReceiptOutbox(string root, string address, string owner)
        {
            var service = BackendConnection.ValidateAddress(address).AbsoluteUri;
            using (var hash = SHA256.Create())
                directory = Path.Combine(root, BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(service + "\n" + owner))).Replace("-", ""));
            gate = gates.GetOrAdd(directory, _ => new SemaphoreSlim(1));
        }
        private static void Validate(Receipt receipt)
        {
            if (receipt == null || !Regex.IsMatch(receipt.SessionId ?? "", "^[a-zA-Z0-9_-]{1,128}$") ||
                !Regex.IsMatch(receipt.RequestId ?? "", "^[a-zA-Z0-9_-]{1,128}$") ||
                !Regex.IsMatch(receipt.Sha256 ?? "", "^[a-f0-9]{64}$"))
                throw new InvalidDataException("Invalid export receipt.");
        }
        public void Enqueue(string sessionId, string requestId, string sha256)
        {
            var receipt = new Receipt { SessionId = sessionId, RequestId = requestId, Sha256 = sha256 };
            Validate(receipt);
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, sessionId + "_" + requestId + ".json");
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(receipt));
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                // The same receipt may already be pending; server receipts are idempotent.
                try { File.Move(temporary, target); }
                catch (IOException) when (File.Exists(target))
                {
                    var existing = JsonConvert.DeserializeObject<Receipt>(File.ReadAllText(target));
                    Validate(existing);
                    if (existing.SessionId != sessionId || existing.RequestId != requestId || existing.Sha256 != sha256) throw;
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public async Task DrainAsync(Func<Receipt, CancellationToken, Task> send, CancellationToken ct,
            Func<int, CancellationToken, Task> retryDelay = null)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!Directory.Exists(directory)) return;
                foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                {
                    ct.ThrowIfCancellationRequested();
                    Receipt receipt;
                    try
                    {
                        if (new FileInfo(path).Length > 4096) continue;
                        receipt = JsonConvert.DeserializeObject<Receipt>(File.ReadAllText(path));
                        Validate(receipt);
                    }
                    catch (Exception error) when (error is IOException || error is JsonException || error is UnauthorizedAccessException) { continue; }
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            await send(receipt, ct).ConfigureAwait(false);
                            File.Delete(path);
                            break;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (LoginRequiredException) { return; }
                        catch (Exception)
                        {
                            if (attempt == 2) break; // Retain this receipt without blocking other sessions.
                            if (retryDelay != null) await retryDelay(attempt, ct).ConfigureAwait(false);
                            else await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 5), ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            finally { gate.Release(); }
        }
    }
}
