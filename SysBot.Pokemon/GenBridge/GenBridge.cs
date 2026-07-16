using PKHeX.Core;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            // Split paste into team chunks
            var chunks = SplitTeamPaste(req.showdownSet);
            if (chunks.Count == 0)
            {
                WriteJson(ctx, 400, new { ok = false, error = "empty set" });
                return;
            }
            if (chunks.Count > 6)
            {
                WriteJson(ctx, 400, new { ok = false, error = "too many sets (max 6)" });
                return;
            }

            // Parse and legalize each chunk
            var trainerInfo = AutoLegalityWrapper.GetTrainerInfo<T>();
            var parsedPkms = new List<PKM>();
            var parseErrors = new List<string>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunkResult = TryParseChunk(trainerInfo, chunks[i], i);
                if (chunkResult is { Success: true, Value: not null })
                    parsedPkms.Add(chunkResult.Value);
                else
                    parseErrors.Add(chunkResult.Error);
            }

            if (parseErrors.Count > 0)
            {
                WriteJson(ctx, 400, new { ok = false, error = string.Join("; ", parseErrors) });
                return;
            }

            // Check for duplicate species+form in the team
            for (int i = 0; i < parsedPkms.Count; i++)
            {
                for (int j = i + 1; j < parsedPkms.Count; j++)
                {
                    if (parsedPkms[i].Species == parsedPkms[j].Species && parsedPkms[i].Form == parsedPkms[j].Form)
                    {
                        WriteJson(ctx, 400, new { ok = false, error = $"duplicate species in team: {SpeciesDisplayName(parsedPkms[i].Species)}" });
                        return;
                    }
                }
            }

            var first = EntityConverter.ConvertToType(parsedPkms[0], typeof(T), out _) ?? parsedPkms[0];
            if (first is not T pk)
            {
                WriteJson(ctx, 400, new { ok = false, error = "conversion failed" });
                return;
            }
            pk.ResetPartyStats();

            // Enqueue
            var trainer = new PokeTradeTrainerInfo(req.trainerName, req.discordId);
            var notifier = new WebhookTradeNotifier<T>(pk, trainer, req.code, req.discordId, req.callbackUrl, req.requestId, secret);
            var detail = new PokeTradeDetail<T>(pk, trainer, notifier, PokeTradeType.EditReturn, req.code, false);

            // Team mode: assign all parsed PKMs as targets
            if (parsedPkms.Count > 1)
            {
                detail.EditReturnTargets = parsedPkms
                    .Select(p => EntityConverter.ConvertToType(p, typeof(T), out _) ?? p)
                    .Cast<T>()
                    .ToList();
            }

            var entry = new TradeEntry<T>(detail, req.discordId, PokeRoutineType.LinkTrade, req.trainerName);

            var info = hub.Queues.Info;
            var added = info.AddToTradeQueue(entry, req.discordId, false);

            if (added == QueueResultAdd.AlreadyInQueue)
            {
                WriteJson(ctx, 400, new { ok = false, error = "already in queue" });
                return;
            }

            var pos = info.CheckPosition(req.discordId, PokeRoutineType.LinkTrade);

            var speciesNames = parsedPkms.Select(p => SpeciesDisplayName(p.Species)).ToList();

            LogUtil.LogText($"gen bridge: requestId={req.requestId} species={(Species)pk.Species} position={pos.Position}");

            WriteJson(ctx, 200, new { ok = true, queued = true, position = pos.Position, count = parsedPkms.Count, species = speciesNames });
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

    private static string SpeciesDisplayName(ushort species)
    {
        var names = GameInfo.GetStrings("en").Species;
        return species == 0 || species >= names.Count ? "Unknown" : names[species];
    }

    /// <summary>
    /// Split a paste into chunks separated by one or more blank lines (empty/whitespace-only).
    /// Handles both \n and \r\n line endings.
    /// </summary>
    public static List<string> SplitTeamPaste(string paste)
    {
        var lines = paste.Split('\n');
        var chunks = new List<string>();
        var current = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Replace("\r", "").Trim();
            if (trimmed.Length == 0)
            {
                // Blank line — flush current chunk if non-empty
                if (current.Count > 0)
                {
                    chunks.Add(string.Join("\n", current));
                    current.Clear();
                }
            }
            else
            {
                current.Add(trimmed);
            }
        }

        // Flush last chunk
        if (current.Count > 0)
            chunks.Add(string.Join("\n", current));

        return chunks;
    }

    /// <summary>
    /// Parse a single Showdown set chunk, legalize, and convert. Returns success PKM or error string.
    /// </summary>
    private static (bool Success, PKM? Value, string Error) TryParseChunk(ITrainerInfo trainerInfo, string chunk, int index)
    {
        var set = new ShowdownSet(chunk);
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

            // Get the first non-empty line for identification
            var firstLine = chunk.Split('\n').First(l => !string.IsNullOrWhiteSpace(l.Replace("\r", "")));
            return (false, null, $"set {index + 1} ({firstLine}): {sb}");
        }

        var template = AutoLegalityWrapper.GetTemplate(set);
        var pkm = trainerInfo.GetLegal(template, out var result);
        var la = new LegalityAnalysis(pkm);

        if (!la.Valid)
        {
            var firstLine = chunk.Split('\n').First(l => !string.IsNullOrWhiteSpace(l.Replace("\r", "")));
            var errorMsg = result switch
            {
                "Timeout" => "legalization timed out",
                "VersionMismatch" => "PKHeX and Auto-Legality Mod version mismatch",
                _ => "couldn't legalize that set",
            };
            return (false, null, $"set {index + 1} ({firstLine}): {errorMsg}");
        }

        return (true, pkm, string.Empty);
    }
}
