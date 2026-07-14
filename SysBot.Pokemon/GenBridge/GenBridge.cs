using PKHeX.Core;
using SysBot.Base;
using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace SysBot.Pokemon.GenBridge;

public static class GenBridge<T> where T : PKM, new()
{
    private sealed record GenRequest(string requestId, int code, ulong discordId, string trainerName, string showdownSet, string callbackUrl);

    public static void TryStart(PokeTradeHub<T> hub)
    {
        var prefix = Environment.GetEnvironmentVariable("GENBRIDGE_PREFIX");
        var secret = Environment.GetEnvironmentVariable("GENBRIDGE_SECRET");

        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(secret))
        {
            LogUtil.LogText("gen bridge disabled (GENBRIDGE_* unset)");
            return;
        }

        if (!prefix.EndsWith("/"))
            prefix += "/";

        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        LogUtil.LogText($"gen bridge listening on {prefix}");

        Task.Run(() =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = listener.GetContext();
                    Task.Run(() => HandleRequest(ctx, hub, secret));
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"gen bridge listener error: {ex.Message}", nameof(GenBridge<T>));
            }
        });
    }

    private static void HandleRequest(HttpListenerContext ctx, PokeTradeHub<T> hub, string secret)
    {
        try
        {
            // Health check
            if (ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                ctx.Request.Url.LocalPath.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(ctx, 200, new { ok = true });
                return;
            }

            // Only accept POST to /editreturn
            if (!ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                !ctx.Request.Url.LocalPath.Equals("/editreturn", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            // Auth check
            var auth = ctx.Request.Headers["Authorization"];
            if (auth == null || auth != "Bearer " + secret)
            {
                WriteJson(ctx, 403, new { ok = false, error = "forbidden" });
                return;
            }

            // Parse body
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var body = ReadBody(ctx);
            GenRequest req;
            try
            {
                req = JsonSerializer.Deserialize<GenRequest>(body, jsonOptions);
            }
            catch
            {
                WriteJson(ctx, 400, new { ok = false, error = "invalid request" });
                return;
            }

            if (req == null || string.IsNullOrEmpty(req.requestId) ||
                string.IsNullOrEmpty(req.trainerName) || string.IsNullOrEmpty(req.showdownSet) ||
                string.IsNullOrEmpty(req.callbackUrl))
            {
                WriteJson(ctx, 400, new { ok = false, error = "invalid request" });
                return;
            }

            // Parse Showdown set
            var set = new ShowdownSet(req.showdownSet);
            var template = AutoLegalityWrapper.GetTemplate(set);
            if (set.InvalidLines.Count != 0 || set.Species is 0)
            {
                var sb = new System.Text.StringBuilder();
                if (set.InvalidLines.Count != 0)
                    sb.Append("Invalid lines: " + string.Join(", ", set.InvalidLines));
                if (set.Species is 0)
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append("species not recognized");
                }
                WriteJson(ctx, 400, new { ok = false, error = sb.ToString() });
                return;
            }

            // Legalize
            var trainerInfo = AutoLegalityWrapper.GetTrainerInfo<T>();
            var pkm = trainerInfo.GetLegal(template, out var result);
            var la = new LegalityAnalysis(pkm);
            pkm = EntityConverter.ConvertToType(pkm, typeof(T), out _) ?? pkm;

            if (pkm is not T pk || !la.Valid)
            {
                var errorMsg = result switch
                {
                    "Timeout" => "legalization timed out",
                    "VersionMismatch" => "PKHeX and Auto-Legality Mod version mismatch",
                    _ => "couldn't legalize that set",
                };
                WriteJson(ctx, 400, new { ok = false, error = errorMsg });
                return;
            }

            pk.ResetPartyStats();

            // Enqueue
            var trainer = new PokeTradeTrainerInfo(req.trainerName, req.discordId);
            var notifier = new WebhookTradeNotifier<T>(pk, trainer, req.code, req.discordId, req.callbackUrl, req.requestId, secret);
            var detail = new PokeTradeDetail<T>(pk, trainer, notifier, PokeTradeType.EditReturn, req.code, false);
            var entry = new TradeEntry<T>(detail, req.discordId, PokeRoutineType.LinkTrade, req.trainerName);

            var info = hub.Queues.Info;
            var added = info.AddToTradeQueue(entry, req.discordId, false);

            if (added == QueueResultAdd.AlreadyInQueue)
            {
                WriteJson(ctx, 400, new { ok = false, error = "already in queue" });
                return;
            }

            var pos = info.CheckPosition(req.discordId, PokeRoutineType.LinkTrade);

            LogUtil.LogText($"gen bridge: requestId={req.requestId} species={(Species)pk.Species} position={pos.Position}");

            WriteJson(ctx, 200, new { ok = true, queued = true, position = pos.Position });
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"gen bridge request error: {ex.Message}", nameof(GenBridge<T>));
            try { WriteJson(ctx, 500, new { ok = false, error = "internal error" }); }
            catch { /* response may already be closed */ }
        }
    }

    private static string ReadBody(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream);
        return reader.ReadToEnd();
    }

    private static void WriteJson(HttpListenerContext ctx, int statusCode, object obj)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(obj);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }
}
