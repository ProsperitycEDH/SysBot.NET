using PKHeX.Core;
using SysBot.Base;
using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SysBot.Pokemon.GenBridge;

public class WebhookTradeNotifier<T>(T data, PokeTradeTrainerInfo info, int code, ulong discordId, string callbackUrl, string requestId, string secret)
    : IPokeTradeNotifier<T> where T : PKM, new()
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public Action<PokeRoutineExecutor<T>>? OnFinish
    {
        private get;
        set;
    }

    public void TradeInitialize(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> detail)
    {
        Post("initializing", "Initializing your trade — be ready to offer your Pokémon.");
    }

    public void TradeSearching(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> detail)
    {
        Post("searching", $"I'm searching for you now. Offer your Pokémon using trade code {code:0000 0000}.");
    }

    public void TradeCanceled(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> detail, PokeTradeResult msg)
    {
        OnFinish?.Invoke(routine);
        Post("canceled", $"Trade canceled: {msg}.");
    }

    public void TradeFinished(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> detail, T result)
    {
        OnFinish?.Invoke(routine);
        Post("finished", "Done — your edited Pokémon is on its way. Deposit it to HOME, then import to Champions.");
    }

    public void SendNotification(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> detail, string message)
    {
        Post("notify", message);
    }

    public void SendNotification(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> detail, PokeTradeSummary message)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(message.Summary);
        if (message.Details.Count > 0)
        {
            var parts = message.Details.Select(z => $"{z.Heading}: {z.Detail}");
            sb.Append(", ").Append(string.Join(", ", parts));
        }
        Post("notify", sb.ToString());
    }

    public void SendNotification(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> detail, T result, string message)
    {
        Post("notify", message);
    }

    private void Post(string state, string message)
    {
        Task.Run(() =>
        {
            try
            {
                var body = new { requestId, discordId, state, message };
                var json = JsonSerializer.Serialize(body);
                using var request = new HttpRequestMessage(HttpMethod.Post, callbackUrl)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
                Http.SendAsync(request).Wait();
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Webhook POST failed: {ex.Message}", nameof(WebhookTradeNotifier<T>));
            }
        });
    }
}
