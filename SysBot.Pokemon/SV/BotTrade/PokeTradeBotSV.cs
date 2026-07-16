using PKHeX.Core;
using PKHeX.Core.Searching;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.PokeDataOffsetsSV;

namespace SysBot.Pokemon;

// ReSharper disable once ClassWithVirtualMembersNeverInherited.Global
public class PokeTradeBotSV(PokeTradeHub<PK9> Hub, PokeBotState Config) : PokeRoutineExecutor9SV(Config), ICountBot
{
    private readonly TradeSettings TradeSettings = Hub.Config.Trade;
    public readonly TradeAbuseSettings AbuseSettings = Hub.Config.TradeAbuse;

    public ICountSettings Counts => TradeSettings;

    private static readonly bool EditReturnDebugDump = Environment.GetEnvironmentVariable("GENBRIDGE_DEBUG_DUMP") is string v
        && (v.Equals("1", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Folder to dump received trade data to.
    /// </summary>
    /// <remarks>If null, will skip dumping.</remarks>
    private readonly IDumper DumpSetting = Hub.Config.Folder;

    /// <summary>
    /// Synchronized start for multiple bots.
    /// </summary>
    public bool ShouldWaitAtBarrier { get; private set; }

    /// <summary>
    /// Tracks failed synchronized starts to attempt to re-sync.
    /// </summary>
    public int FailedBarrier { get; private set; }

    // Cached offsets that stay the same per session.
    private ulong BoxStartOffset;
    private ulong OverworldOffset;
    private ulong PortalOffset;
    private ulong ConnectedOffset;
    private ulong TradePartnerNIDOffset;
    private ulong TradePartnerOfferedOffset;

    // Store the current save's OT and TID/SID for comparison.
    private string OT = string.Empty;
    private uint DisplaySID;
    private uint DisplayTID;

    // Stores whether we returned all the way to the overworld, which repositions the cursor.
    private bool StartFromOverworld = true;
    // Stores whether the last trade was Distribution with fixed code, in which case we don't need to re-enter the code.
    private bool LastTradeDistributionFixed;

    // Track the last Pokémon we were offered since it persists between trades.
    private byte[] lastOffered = new byte[8];

    public override async Task MainLoop(CancellationToken token)
    {
        try
        {
            await InitializeHardware(Hub.Config.Trade, token).ConfigureAwait(false);

            Log("Identifying trainer data of the host console.");
            var sav = await IdentifyTrainer(token).ConfigureAwait(false);
            OT = sav.OT;
            DisplaySID = sav.DisplaySID;
            DisplayTID = sav.DisplayTID;
            RecentTrainerCache.SetRecentTrainer(sav);
            await InitializeSessionOffsets(token).ConfigureAwait(false);

            // Force the bot to go through all the motions again on its first pass.
            StartFromOverworld = true;
            LastTradeDistributionFixed = false;

            Log($"Starting main {nameof(PokeTradeBotSV)} loop.");
            await InnerLoop(sav, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log($"Trade loop ended by exception: {e}");
        }

        Log($"Ending {nameof(PokeTradeBotSV)} loop.");

        // Cleanup presses buttons on the console, which is unreachable in exactly the failure
        // case that ends the loop — a cleanup failure must not escape and kill the executor.
        try
        {
            await HardStop().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Exit cleanup failed (console unreachable?): {ex.Message}");
        }
    }

    public override Task HardStop()
    {
        UpdateBarrier(false);
        return CleanExit(CancellationToken.None);
    }

    private async Task InnerLoop(SAV9SV sav, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Config.IterateNextRoutine();
            var task = Config.CurrentRoutineType switch
            {
                PokeRoutineType.Idle => DoNothing(token),
                _ => DoTrades(sav, token),
            };
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (SocketException e)
            {
                if (e.StackTrace != null)
                    Connection.LogError(e.StackTrace);
                var attempts = Hub.Config.Timings.ReconnectAttempts;
                var delay = Hub.Config.Timings.ExtraReconnectDelay;
                var protocol = Config.Connection.Protocol;
                if (!await TryReconnect(attempts, delay, protocol, token).ConfigureAwait(false))
                    return;
            }
        }
    }

    private async Task DoNothing(CancellationToken token)
    {
        Log("No task assigned. Waiting for new task assignment.");
        while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.Idle)
            await Task.Delay(1_000, token).ConfigureAwait(false);
    }

    private async Task DoTrades(SAV9SV sav, CancellationToken token)
    {
        var type = Config.CurrentRoutineType;
        int waitCounter = 0;
        await SetCurrentBox(0, token).ConfigureAwait(false);
        while (!token.IsCancellationRequested && Config.NextRoutineType == type)
        {
            var (detail, priority) = GetTradeData(type);
            if (detail is null)
            {
                await WaitForQueueStep(waitCounter++, token).ConfigureAwait(false);
                continue;
            }
            waitCounter = 0;

            detail.IsProcessing = true;
            string tradetype = $" ({detail.Type})";
            Log($"Starting next {type}{tradetype} Bot Trade. Getting data...");
            Hub.Config.Stream.StartTrade(this, detail, Hub);
            Hub.Queues.StartTrade(this, detail);

            await PerformTrade(sav, detail, type, priority, token).ConfigureAwait(false);
        }
    }

    private Task WaitForQueueStep(int waitCounter, CancellationToken token)
    {
        if (waitCounter == 0)
        {
            // Updates the assets.
            Hub.Config.Stream.IdleAssets(this);
            Log("Nothing to check, waiting for new users...");
        }

        return Task.Delay(1_000, token);
    }

    protected virtual (PokeTradeDetail<PK9>? detail, uint priority) GetTradeData(PokeRoutineType type)
    {
        if (Hub.Queues.TryDequeue(type, out var detail, out var priority))
            return (detail, priority);
        if (Hub.Queues.TryDequeueLedy(out detail))
            return (detail, PokeTradePriorities.TierFree);
        return (null, PokeTradePriorities.TierFree);
    }

    private async Task PerformTrade(SAV9SV sav, PokeTradeDetail<PK9> detail, PokeRoutineType type, uint priority, CancellationToken token)
    {
        // Team mode: loop for consecutive trades
        bool teamMode = detail.EditReturnTargets is { Count: > 0 };
        int initialRemaining = detail.EditReturnTargets?.Count ?? 0;
        int attempt = 0;
        int cap = 2 * initialRemaining + 2;

        PokeTradeResult result = PokeTradeResult.Success;
        while (true)
        {
            attempt++;
            // Set TradeData for this attempt (first = already set by enqueuer; subsequent = next target)
            if (attempt > 1 && detail.EditReturnTargets is { Count: > 0 })
                detail.TradeData = detail.EditReturnTargets[0];

            result = await PerformOneTradeAttempt(sav, detail, type, priority, token).ConfigureAwait(false);

            // Team mode: check continuation
            if (teamMode && detail.EditReturnTargets is { Count: > 0 })
            {
                if (ShouldContinueEditReturnSession(detail.EditReturnTargets.Count, attempt, cap, result))
                {
                    continue;
                }
            }

            // Session end — break out
            break;
        }

        // Session-end summary for team mode
        if (teamMode)
        {
            var remaining = detail.EditReturnTargets ?? [];
            var parts = new List<string>();
            if (detail.EditReturnDone.Count > 0)
                parts.Add($"edited: {string.Join(", ", detail.EditReturnDone)}");
            if (detail.EditReturnFailed.Count > 0)
                parts.Add($"failed: {string.Join("; ", detail.EditReturnFailed)}");
            if (remaining.Count > 0)
                parts.Add($"not traded: {string.Join(", ", remaining.Select(t => GetSpeciesName(t.Species)))}");
            var summary = "Team session ended — " + string.Join(", ", parts) + ".";
            if (result == PokeTradeResult.NoTrainerFound && remaining.Count > 0)
                summary += " Run /gen again with the remaining sets to finish.";
            detail.SendNotification(this, summary);
        }

        if (result == PokeTradeResult.Success)
            return;

        HandleAbortedTrade(detail, type, priority, result);
    }

    private async Task<PokeTradeResult> PerformOneTradeAttempt(SAV9SV sav, PokeTradeDetail<PK9> detail, PokeRoutineType type, uint priority, CancellationToken token)
    {
        try
        {
            return await PerformLinkCodeTrade(sav, detail, token).ConfigureAwait(false);
        }
        catch (SocketException socket)
        {
            Log(socket.Message);
            HandleAbortedTrade(detail, type, priority, PokeTradeResult.ExceptionConnection);
            throw; // let this interrupt the trade loop. re-entering the trade loop will recheck the connection.
        }
        catch (Exception e)
        {
            Log(e.Message);
            return PokeTradeResult.ExceptionInternal;
        }
    }

    private void HandleAbortedTrade(PokeTradeDetail<PK9> detail, PokeRoutineType type, uint priority, PokeTradeResult result)
    {
        detail.IsProcessing = false;
        if (result.ShouldAttemptRetry() && detail.Type != PokeTradeType.Random && !detail.IsRetry)
        {
            detail.IsRetry = true;
            Hub.Queues.Enqueue(type, detail, Math.Min(priority, PokeTradePriorities.Tier2));
            detail.SendNotification(this, "Oops! Something happened. I'll requeue you for another attempt.");
        }
        else
        {
            detail.SendNotification(this, $"Oops! Something happened. Canceling the trade: {result}.");
            detail.TradeCanceled(this, result);
        }
    }

    private async Task<PokeTradeResult> PerformLinkCodeTrade(SAV9SV sav, PokeTradeDetail<PK9> poke, CancellationToken token)
    {
        // Update Barrier Settings
        UpdateBarrier(poke.IsSynchronized);
        poke.TradeInitialize(this);
        Hub.Config.Stream.EndEnterCode(this);

        // Always begin each trade from the overworld and run the full, deterministic portal
        // navigation. The fast in-portal "reorient" path assumes the cursor is still on Link
        // Trade, which is false whenever the Poke Portal news popup interrupts navigation --
        // that mismatch made the bot mash the code/confirm buttons on the wrong screen.
        // Full re-navigation each trade is slightly slower but immune to that.
        StartFromOverworld = true;

        // Refresh cached session offsets each trade so stale addresses (from an odd
        // game state at startup) don't cause IsConnectedOnline to misreport.
        await InitializeSessionOffsets(token).ConfigureAwait(false);

        // StartFromOverworld can be true on first pass or if something went wrong last trade.
        if (StartFromOverworld && !await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
        {
            await RecoverToOverworld(token).ConfigureAwait(false);
        }

        // Handles getting into the portal. Will retry this until successful.
        // if we're not starting from overworld, then ensure we're online before opening link trade -- will break the bot otherwise.
        // If we're starting from overworld, then ensure we're online before opening the portal.
        if (!StartFromOverworld && !await RefreshAndConfirmOnlineState(token).ConfigureAwait(false))
        {
            await CaptureNavigationFailureAsync("not-online", token).ConfigureAwait(false);
            await RecoverToOverworld(token).ConfigureAwait(false);
            if (!await ConnectAndEnterPortal(token).ConfigureAwait(false))
            {
                await RecoverToOverworld(token).ConfigureAwait(false);
                return PokeTradeResult.RecoverStart;
            }
        }
        else if (StartFromOverworld && !await ConnectAndEnterPortal(token).ConfigureAwait(false))
        {
            await RecoverToOverworld(token).ConfigureAwait(false);
            return PokeTradeResult.RecoverStart;
        }

        // Post-navigation online gate: confirm online with stable fresh check.
        if (!await RefreshAndConfirmOnlineState(token).ConfigureAwait(false))
        {
            Log("Console is not online after portal navigation; recovering.");
            await CaptureNavigationFailureAsync("post-nav-not-online", token).ConfigureAwait(false);
            await RecoverToOverworld(token).ConfigureAwait(false);
            return PokeTradeResult.RecoverStart;
        }

        var toSend = poke.TradeData;
        if (toSend.Species != 0)
            await SetBoxPokemonAbsolute(BoxStartOffset, toSend, token, sav).ConfigureAwait(false);

        // Assumes we're freshly in the Portal and the cursor is over Link Trade.
        Log("Selecting Link Trade.");

        await Click(A, 1_500, token).ConfigureAwait(false);
        // Make sure we clear any Link Codes if we're not in Distribution with fixed code, and it wasn't entered last round.
        if (poke.Type != PokeTradeType.Random || !LastTradeDistributionFixed)
        {
            await Click(X, 1_000, token).ConfigureAwait(false);
            await Click(PLUS, 1_000, token).ConfigureAwait(false);

            // Loading code entry.
            if (poke.Type != PokeTradeType.Random)
                Hub.Config.Stream.StartEnterCode(this);
            await Task.Delay(Hub.Config.Timings.ExtraTimeOpenCodeEntry, token).ConfigureAwait(false);

            var code = poke.Code;
            Log($"Entering Link Trade code: {code:0000 0000}...");
            await EnterLinkCode(code, Hub.Config, token).ConfigureAwait(false);

            await Click(PLUS, 3_000, token).ConfigureAwait(false);
            StartFromOverworld = false;
        }

        LastTradeDistributionFixed = poke.Type == PokeTradeType.Random && !Hub.Config.Distribution.RandomCode;

        // Search for a trade partner for a Link Trade.
        // NOTE: There is no proven memory signal for the code-entry or search-dialog state,
        // so we cannot validate exact cursor position. The post-code confirmation A presses
        // are hardware-proven and remain in place. Navigation failure captures (CaptureNavigationFailureAsync)
        // are intended to supply evidence for future pointer/visual gating of these states.
        // The confirmation can lag behind an online check, so give it a moment to render,
        // then press A a few times with generous spacing to reliably hit Yes.
        // Extra A presses once searching has begun are harmless.
        await Task.Delay(1_500, token).ConfigureAwait(false);
        await Click(A, 1_200, token).ConfigureAwait(false);
        await Click(A, 1_200, token).ConfigureAwait(false);

        // Clear it so we can detect it loading.
        await ClearTradePartnerNID(TradePartnerNIDOffset, token).ConfigureAwait(false);

        // Wait for Barrier to trigger all bots simultaneously.
        WaitAtBarrierIfApplicable(token);
        await Click(A, 1_000, token).ConfigureAwait(false);
        await Click(A, 1_000, token).ConfigureAwait(false);

        poke.TradeSearching(this);

        // Wait for a Trainer...
        var partnerFound = await WaitForTradePartner(token).ConfigureAwait(false);

        if (token.IsCancellationRequested)
        {
            StartFromOverworld = true;
            LastTradeDistributionFixed = false;
            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return PokeTradeResult.RoutineCancel;
        }
        if (!partnerFound)
        {
            // Recover directly to overworld — consistent with every next trade starting from overworld.
            await RecoverToOverworld(token).ConfigureAwait(false);
            return PokeTradeResult.NoTrainerFound;
        }

        Hub.Config.Stream.EndEnterCode(this);

        // Wait until we get into the box.
        var cnt = 0;
        while (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
        {
            await Task.Delay(0_500, token).ConfigureAwait(false);
            if (++cnt > 20) // Didn't make it in after 10 seconds.
            {
                await CaptureNavigationFailureAsync("box-entry-failed", token).ConfigureAwait(false);
                return await RecoverOpenBox(token).ConfigureAwait(false);
            }
        }
        await Task.Delay(3_000 + Hub.Config.Timings.ExtraTimeOpenBox, token).ConfigureAwait(false);

        var tradePartner = await GetTradePartnerInfo(token).ConfigureAwait(false);
        var trainerNID = await GetTradePartnerNID(TradePartnerNIDOffset, token).ConfigureAwait(false);
        RecordUtil<PokeTradeBotSV>.Record($"Initiating\t{trainerNID:X16}\t{tradePartner.TrainerName}\t{poke.Trainer.TrainerName}\t{poke.Trainer.ID}\t{poke.ID}\t{toSend.EncryptionConstant:X8}");
        Log($"Found Link Trade partner: {tradePartner.TrainerName}-{tradePartner.TID7} (ID: {trainerNID})");

        var partnerCheck = await CheckPartnerReputation(this, poke, trainerNID, tradePartner.TrainerName, AbuseSettings, token);
        if (partnerCheck != PokeTradeResult.Success)
        {
            await Click(A, 1_000, token).ConfigureAwait(false); // Ensures we dismiss a popup.
            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return partnerCheck;
        }

        // Hard check to verify that the offset changed from the last thing offered from the previous trade.
        // This is because box opening times can vary per person, the offset persists between trades, and can also change offset between trades.
        var tradeOffered = await ReadUntilChanged(TradePartnerOfferedOffset, lastOffered, 10_000, 0_500, false, true, token).ConfigureAwait(false);
        if (!tradeOffered)
        {
            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return PokeTradeResult.TrainerTooSlow;
        }

        poke.SendNotification(this, $"Found Link Trade partner: {tradePartner.TrainerName}. Waiting for a Pokémon...");

        if (poke.Type == PokeTradeType.Dump)
        {
            var result = await ProcessDumpTradeAsync(poke, token).ConfigureAwait(false);
            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return result;
        }

        // Wait for user input...
        var offered = await ReadUntilPresent(TradePartnerOfferedOffset, 25_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
        var oldEC = await SwitchConnection.ReadBytesAbsoluteAsync(TradePartnerOfferedOffset, 8, token).ConfigureAwait(false);
        if (offered == null || offered.Species == 0 || !offered.ChecksumValid)
        {
            Log("Trade ended because a valid Pokémon was not offered.");
            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return PokeTradeResult.TrainerTooSlow;
        }

        var trainer = new PartnerDataHolder(0, tradePartner.TrainerName, tradePartner.TID7);
        (toSend, PokeTradeResult update) = await GetEntityToSend(sav, poke, offered, oldEC, toSend, trainer, token).ConfigureAwait(false);
        if (update != PokeTradeResult.Success)
        {
            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return update;
        }

        if (Hub.Config.Trade.DisallowTradeEvolve && TradeEvolutions.WillTradeEvolve(offered.Species, offered.Form, offered.HeldItem, toSend.Species))
        {
            Log("Trade cancelled because trainer offered a Pokémon that would evolve upon trade.");
            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return PokeTradeResult.TradeEvolveNotAllowed;
        }

        Log("Confirming trade.");
        var tradeResult = await ConfirmAndStartTrading(poke, token).ConfigureAwait(false);
        if (tradeResult != PokeTradeResult.Success)
        {
            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return tradeResult;
        }

        if (token.IsCancellationRequested)
        {
            StartFromOverworld = true;
            LastTradeDistributionFixed = false;
            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return PokeTradeResult.RoutineCancel;
        }

        // Trade was successful!
        var received = await ReadPokemon(BoxStartOffset, BoxFormatSlotSize, token).ConfigureAwait(false);
        // Pokémon in b1s1 is same as the one they were supposed to receive (was never sent).
        if (SearchUtil.HashByDetails(received) == SearchUtil.HashByDetails(toSend) && received.Checksum == toSend.Checksum)
        {
            Log("User did not complete the trade.");
            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return PokeTradeResult.TrainerTooSlow;
        }

        // As long as we got rid of our inject in b1s1, assume the trade went through.
        Log("User completed the trade.");
        poke.TradeFinished(this, received);

        // Only log if we completed the trade.
        UpdateCountsAndExport(poke, received, toSend);

        // Log for Trade Abuse tracking.
        LogSuccessfulTrades(poke, trainerNID, tradePartner.TrainerName);

        // Sometimes they offered another mon, so store that immediately upon leaving Union Room.
        lastOffered = await SwitchConnection.ReadBytesAbsoluteAsync(TradePartnerOfferedOffset, 8, token).ConfigureAwait(false);

        await ExitTradeToPortal(false, token).ConfigureAwait(false);
        return PokeTradeResult.Success;
    }

    private async Task<PokeTradeResult> RecoverOpenBox(CancellationToken token)
    {
        // Recover directly to overworld rather than re-entering Portal.
        await Click(A, 1_000, token).ConfigureAwait(false); // Ensures we dismiss a popup.
        await RecoverToOverworld(token).ConfigureAwait(false);
        return PokeTradeResult.RecoverOpenBox;
    }

    private void UpdateCountsAndExport(PokeTradeDetail<PK9> poke, PK9 received, PK9 toSend)
    {
        var counts = TradeSettings;
        if (poke.Type == PokeTradeType.Random)
            counts.AddCompletedDistribution();
        else if (poke.Type == PokeTradeType.Clone)
            counts.AddCompletedClones();
        else
            counts.AddCompletedTrade();

        if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
        {
            var subfolder = poke.Type.ToString().ToLower();
            DumpPokemon(DumpSetting.DumpFolder, subfolder, received); // received by bot
            if (poke.Type is PokeTradeType.Specific or PokeTradeType.Clone)
                DumpPokemon(DumpSetting.DumpFolder, "traded", toSend); // sent to partner
        }
    }

    private async Task<PokeTradeResult> ConfirmAndStartTrading(PokeTradeDetail<PK9> detail, CancellationToken token)
    {
        // We'll keep watching B1S1 for a change to indicate a trade started -> should try quitting at that point.
        var oldEC = await SwitchConnection.ReadBytesAbsoluteAsync(BoxStartOffset, 8, token).ConfigureAwait(false);

        await Click(A, 3_000, token).ConfigureAwait(false);
        for (int i = 0; i < Hub.Config.Trade.MaxTradeConfirmTime; i++)
        {
            if (await IsUserBeingShifty(detail, token).ConfigureAwait(false))
                return PokeTradeResult.SuspiciousActivity;

            // We can fall out of the box if the user offers, then quits.
            if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
                return PokeTradeResult.TrainerLeft;

            await Click(A, 1_000, token).ConfigureAwait(false);

            // EC is detectable at the start of the animation.
            var newEC = await SwitchConnection.ReadBytesAbsoluteAsync(BoxStartOffset, 8, token).ConfigureAwait(false);
            if (!newEC.SequenceEqual(oldEC))
            {
                await Task.Delay(25_000, token).ConfigureAwait(false);
                return PokeTradeResult.Success;
            }
        }
        // If we don't detect a B1S1 change, the trade didn't go through in that time.
        return PokeTradeResult.TrainerTooSlow;
    }

    // Upon connecting, their Nintendo ID will instantly update.
    // Uses elapsed real time against TradeWaitTime seconds so the configured value is
    // approximately the real wait duration (no hidden uncounted delays).
    protected virtual async Task<bool> WaitForTradePartner(CancellationToken token)
    {
        Log("Waiting for trainer...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int waitMs = Hub.Config.Trade.TradeWaitTime * 1_000;
        // Poll about once per second with no extra uncounted delay.
        while (sw.ElapsedMilliseconds < waitMs)
        {
            await Task.Delay(1_000, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
                return false;

            var newNID = await GetTradePartnerNID(TradePartnerNIDOffset, token).ConfigureAwait(false);

            if (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                Log("Console went offline while searching for a partner.");
                return false;
            }

            if (newNID != 0)
            {
                TradePartnerOfferedOffset = await SwitchConnection.PointerAll(Offsets.LinkTradePartnerPokemonPointer, token).ConfigureAwait(false);
                return true;
            }
        }
        return false;
    }

    // If we can't manually recover to overworld, reset the game.
    // Try to avoid pressing A which can put us back in the portal with the long load time.
    // Limited to at most 5 bounded cycles (10 B presses + box handling) before restarting.
    private async Task<bool> RecoverToOverworld(CancellationToken token)
    {
        if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            return true;

        Log("Attempting to recover to overworld.");
        int cycles = 0;
        const int maxCycles = 5;
        while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
        {
            cycles++;
            if (cycles > maxCycles)
                break;

            await Click(B, 1_000, token).ConfigureAwait(false);
            if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                break;

            await Click(B, 1_000, token).ConfigureAwait(false);
            if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                break;

            if (await IsInBox(PortalOffset, token).ConfigureAwait(false))
                await Click(A, 1_000, token).ConfigureAwait(false);
        }

        // We didn't make it for some reason — restart the game.
        if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
        {
            await CaptureNavigationFailureAsync("overworld-recovery-failed", token).ConfigureAwait(false);
            Log("Failed to recover to overworld in 5 cycles, rebooting the game.");
            await RestartGameSV(token).ConfigureAwait(false);
        }
        await Task.Delay(1_000, token).ConfigureAwait(false);

        // Force the bot to go through all the motions again on its first pass.
        StartFromOverworld = true;
        LastTradeDistributionFixed = false;
        return true;
    }

    private async Task DismissNewsIfShowing(CancellationToken token)
    {
        int iterations = 0;
        while (await SwitchConnection.IsProgramRunning(LibAppletWeID, token).ConfigureAwait(false))
        {
            if (iterations == 0)
                Log("News detected, dismissing...");
            await Task.Delay(2_000, token).ConfigureAwait(false);
            await Click(B, 1_000, token).ConfigureAwait(false);
            if (++iterations >= 10)
            {
                Log("News popup still showing after 10 dismissal attempts; giving up.");
                break;
            }
        }
    }

    // Should be used from the overworld. Opens X menu, attempts to connect online, and enters the Portal.
    // The cursor should be positioned over Link Trade.
    private async Task<bool> ConnectAndEnterPortal(CancellationToken token)
    {
        if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            await RecoverToOverworld(token).ConfigureAwait(false);

        Log("Opening the Poké Portal.");

        // Open the X Menu.
        await Click(X, 1_000, token).ConfigureAwait(false);

        await DismissNewsIfShowing(token).ConfigureAwait(false);

        // Scroll to the bottom of the Main Menu, so we don't need to care if Picnic is unlocked.
        await Click(DRIGHT, 0_300, token).ConfigureAwait(false);
        await PressAndHold(DDOWN, 1_000, 1_000, token).ConfigureAwait(false);
        await Click(DUP, 0_200, token).ConfigureAwait(false);
        await Click(DUP, 0_200, token).ConfigureAwait(false);
        await Click(DUP, 0_200, token).ConfigureAwait(false);
        await Click(A, 1_000, token).ConfigureAwait(false);

        return await SetUpPortalCursor(token).ConfigureAwait(false);
    }

    // Waits for the Portal to load (slow) and then moves the cursor down to Link Trade.
    private async Task<bool> SetUpPortalCursor(CancellationToken token)
    {
        // Wait for the portal to load.
        var attempts = 0;
        while (!await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
        {
            await Task.Delay(0_500, token).ConfigureAwait(false);
            if (++attempts > 20)
            {
                Log("Failed to load the Poké Portal.");
                await CaptureNavigationFailureAsync("portal-load-failed", token).ConfigureAwait(false);
                return false;
            }
        }
        await Task.Delay(2_000 + Hub.Config.Timings.ExtraTimeLoadPortal, token).ConfigureAwait(false);

        // Connect online if not already.
        if (!await ConnectToOnline(Hub.Config, token).ConfigureAwait(false))
        {
            Log("Failed to connect to online.");
            await CaptureNavigationFailureAsync("online-connect-failed", token).ConfigureAwait(false);
            return false; // Failed, either due to connection or softban.
        }

        await DismissNewsIfShowing(token).ConfigureAwait(false);

        // Stable portal-ready check before moving the cursor toward Link Trade.
        if (!await ConfirmPortalReady(token).ConfigureAwait(false))
        {
            Log("Portal not stable after popup handling; aborting cursor setup.");
            await CaptureNavigationFailureAsync("portal-not-ready", token).ConfigureAwait(false);
            return false;
        }

        Log("Adjusting the cursor in the Portal.");
        // Move down to Link Trade.
        await Click(DDOWN, 0_300, token).ConfigureAwait(false);
        await Click(DDOWN, 0_300, token).ConfigureAwait(false);
        return true;
    }

    // Connects online if not already. Assumes the user to be in the X menu to avoid a news screen.
    private async Task<bool> ConnectToOnline(PokeTradeHubConfig config, CancellationToken token)
    {
        if (await RefreshAndConfirmOnlineState(token).ConfigureAwait(false))
            return true;

        await Click(L, 1_000, token).ConfigureAwait(false);
        await Click(A, 4_000, token).ConfigureAwait(false);

        // Real 15-second elapsed deadline.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 15_000)
        {
            await Task.Delay(500, token).ConfigureAwait(false);
            if (await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
                break;
        }

        // There are several seconds after connection is established before we can dismiss the menu.
        await Task.Delay(3_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
        await Click(A, 1_000, token).ConfigureAwait(false);

        // Do not return success until fresh stable helper confirms.
        if (!await RefreshAndConfirmOnlineState(token).ConfigureAwait(false))
        {
            Log("Online not stable after connect sequence.");
            return false;
        }

        return true;
    }

    private async Task ExitTradeToPortal(bool unexpected, CancellationToken token)
    {
        await Task.Delay(1_000, token).ConfigureAwait(false);
        if (await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
            return;

        if (unexpected)
            Log("Unexpected behavior, recovering to Portal.");

        // Ensure we're not in the box first.
        // Takes a long time for the Portal to load up, so once we exit the box, wait 5 seconds.
        Log("Leaving the box...");
        var attempts = 0;
        while (await IsInBox(PortalOffset, token).ConfigureAwait(false))
        {
            await Click(B, 1_000, token).ConfigureAwait(false);
            if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                break;
            }

            await Click(A, 1_000, token).ConfigureAwait(false);
            if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                break;
            }

            await Click(B, 1_000, token).ConfigureAwait(false);
            if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                break;
            }

            // Didn't make it out of the box for some reason — prefer overworld recovery.
            if (++attempts > 20)
            {
                Log("Failed to exit box, recovering to overworld.");
                await CaptureNavigationFailureAsync("box-exit-failed", token).ConfigureAwait(false);
                await RecoverToOverworld(token).ConfigureAwait(false);
                return;
            }
        }

        // Wait for the portal to load.
        Log("Waiting on the portal to load...");
        attempts = 0;
        while (!await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
        {
            await Task.Delay(1_000, token).ConfigureAwait(false);
            if (await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
                break;

            // Didn't make it into the portal for some reason — prefer overworld recovery.
            if (++attempts > 40)
            {
                Log("Failed to load the portal, recovering to overworld.");
                await CaptureNavigationFailureAsync("portal-load-failed", token).ConfigureAwait(false);
                await RecoverToOverworld(token).ConfigureAwait(false);
                return;
            }
        }
    }

    // These don't change per session, and we access them frequently, so set these each time we start.
    private async Task InitializeSessionOffsets(CancellationToken token)
    {
        Log("Caching session offsets...");
        BoxStartOffset = await SwitchConnection.PointerAll(Offsets.BoxStartPokemonPointer, token).ConfigureAwait(false);
        OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
        PortalOffset = await SwitchConnection.PointerAll(Offsets.PortalBoxStatusPointer, token).ConfigureAwait(false);
        ConnectedOffset = await SwitchConnection.PointerAll(Offsets.IsConnectedPointer, token).ConfigureAwait(false);
        TradePartnerNIDOffset = await SwitchConnection.PointerAll(Offsets.LinkTradePartnerNIDPointer, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-resolves the online-connection pointer and requires three consecutive 0x01 samples.
    /// </summary>
    private async Task<bool> RefreshAndConfirmOnlineState(CancellationToken token)
    {
        ConnectedOffset = await SwitchConnection.PointerAll(Offsets.IsConnectedPointer, token).ConfigureAwait(false);
        return await ConfirmStateByte(ConnectedOffset, 0x01, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Requires three consecutive 0x10 samples from the portal/box offset and ensures the news
    /// applet is not running. This proves a stable uncovered Portal state, not a cursor location.
    /// </summary>
    private async Task<bool> ConfirmPortalReady(CancellationToken token)
    {
        if (await SwitchConnection.IsProgramRunning(LibAppletWeID, token).ConfigureAwait(false))
            return false;
        return await ConfirmStateByte(PortalOffset, 0x10, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort diagnostic capture: logs a snapshot line and saves a screenshot (if available)
    /// to <c>records/navigation-failures/</c>. Never throws; catches and logs diagnostic exceptions.
    /// </summary>
    private async Task CaptureNavigationFailureAsync(string stage, CancellationToken token)
    {
        try
        {
            string sanitized = string.Concat(stage.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')).Replace("_", "-").Trim('-');
            if (string.IsNullOrEmpty(sanitized)) sanitized = "unknown";

            // Read the three state bytes (sequential best-effort reads).
            byte overworldVal = 0, portalVal = 0, onlineVal = 0;
            try
            {
                var owBytes = await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 1, token).ConfigureAwait(false);
                overworldVal = owBytes[0];
            }
            catch { /* best-effort */ }
            try
            {
                var ptBytes = await SwitchConnection.ReadBytesAbsoluteAsync(PortalOffset, 1, token).ConfigureAwait(false);
                portalVal = ptBytes[0];
            }
            catch { /* best-effort */ }
            try
            {
                var onBytes = await SwitchConnection.ReadBytesAbsoluteAsync(ConnectedOffset, 1, token).ConfigureAwait(false);
                onlineVal = onBytes[0];
            }
            catch { /* best-effort */ }

            string baseName = $"{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss.fff}_{sanitized}";
            string dir = Path.Combine(Environment.CurrentDirectory, "records", "navigation-failures");
            Directory.CreateDirectory(dir);

            string snapshotLine = $"stage={sanitized} overworldAddr=0x{OverworldOffset:X16} overworld=0x{overworldVal:X2} portalAddr=0x{PortalOffset:X16} portal=0x{portalVal:X2} onlineAddr=0x{ConnectedOffset:X16} online=0x{onlineVal:X2}";
            string logPath = Path.Combine(dir, $"{baseName}.log");
            await File.WriteAllTextAsync(logPath, snapshotLine + "\n", token).ConfigureAwait(false);
            Log($"Navigation failure snapshot: {snapshotLine}");

            // Screenshot capture.
            byte[] image = await SwitchConnection.PixelPeek(token).ConfigureAwait(false);
            if (image != null && image.Length > 0)
            {
                string imgPath = Path.Combine(dir, $"{baseName}.jpg");
                await File.WriteAllBytesAsync(imgPath, image, token).ConfigureAwait(false);
                Log($"Navigation failure screenshot saved: {imgPath}");
            }
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation — don't mask it.
            throw;
        }
        catch (Exception ex)
        {
            Log($"Navigation failure capture failed: {ex.Message}");
        }
    }

    // todo: future
    protected virtual async Task<bool> IsUserBeingShifty(PokeTradeDetail<PK9> detail, CancellationToken token)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return false;
    }

    private async Task RestartGameSV(CancellationToken token)
    {
        await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
        await InitializeSessionOffsets(token).ConfigureAwait(false);
    }

    private async Task<PokeTradeResult> ProcessDumpTradeAsync(PokeTradeDetail<PK9> detail, CancellationToken token)
    {
        int ctr = 0;
        var time = TimeSpan.FromSeconds(Hub.Config.Trade.MaxDumpTradeTime);
        var start = DateTime.Now;

        var pkprev = new PK9();
        var bctr = 0;
        while (ctr < Hub.Config.Trade.MaxDumpsPerTrade && DateTime.Now - start < time)
        {
            if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
                break;
            if (bctr++ % 3 == 0)
                await Click(B, 0_100, token).ConfigureAwait(false);

            // Wait for user input... Needs to be different from the previously offered Pokémon.
            var pk = await ReadUntilPresent(TradePartnerOfferedOffset, 3_000, 0_050, BoxFormatSlotSize, token).ConfigureAwait(false);
            if (pk == null || pk.Species == 0 || !pk.ChecksumValid || SearchUtil.HashByDetails(pk) == SearchUtil.HashByDetails(pkprev))
                continue;

            // Save the new Pokémon for comparison next round.
            pkprev = pk;

            // Send results from separate thread; the bot doesn't need to wait for things to be calculated.
            if (DumpSetting.Dump)
            {
                var subfolder = detail.Type.ToString().ToLower();
                DumpPokemon(DumpSetting.DumpFolder, subfolder, pk); // received
            }

            var la = new LegalityAnalysis(pk);
            var verbose = $"```{la.Report(true)}```";
            Log($"Shown Pokémon is: {(la.Valid ? "Valid" : "Invalid")}.");

            ctr++;
            var msg = Hub.Config.Trade.DumpTradeLegalityCheck ? verbose : $"File {ctr}";

            // Extra information about trainer data for people requesting with their own trainer data.
            var ot = pk.OriginalTrainerName;
            var ot_gender = pk.OriginalTrainerGender == 0 ? "Male" : "Female";
            var tid = pk.GetDisplayTID().ToString(pk.GetTrainerIDFormat().GetTrainerIDFormatStringTID());
            var sid = pk.GetDisplaySID().ToString(pk.GetTrainerIDFormat().GetTrainerIDFormatStringSID());
            msg += $"\n**Trainer Data**\n```OT: {ot}\nOTGender: {ot_gender}\nTID: {tid}\nSID: {sid}```";

            // Extra information for shiny eggs, because of people dumping to skip hatching.
            var eggstring = pk.IsEgg ? "Egg " : string.Empty;
            msg += pk.IsShiny ? $"\n**This Pokémon {eggstring}is shiny!**" : string.Empty;
            detail.SendNotification(this, pk, msg);
        }

        Log($"Ended Dump loop after processing {ctr} Pokémon.");
        if (ctr == 0)
            return PokeTradeResult.TrainerTooSlow;

        TradeSettings.AddCompletedDumps();
        detail.Notifier.SendNotification(this, detail, $"Dumped {ctr} Pokémon.");
        detail.Notifier.TradeFinished(this, detail, detail.TradeData); // blank PK9
        return PokeTradeResult.Success;
    }

    private async Task<TradePartnerSV> GetTradePartnerInfo(CancellationToken token)
    {
        // We're able to see both users' MyStatus, but one of them will be ourselves.
        var trader_info = await GetTradePartnerMyStatus(Offsets.Trader1MyStatusPointer, token).ConfigureAwait(false);
        if (trader_info.OT == OT && trader_info.DisplaySID == DisplaySID && trader_info.DisplayTID == DisplayTID) // This one matches ourselves.
            trader_info = await GetTradePartnerMyStatus(Offsets.Trader2MyStatusPointer, token).ConfigureAwait(false);
        return new TradePartnerSV(trader_info);
    }

    protected virtual async Task<(PK9 toSend, PokeTradeResult check)> GetEntityToSend(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, byte[] oldEC, PK9 toSend, PartnerDataHolder partnerID, CancellationToken token)
    {
        return poke.Type switch
        {
            PokeTradeType.Random => await HandleRandomLedy(sav, poke, offered, toSend, partnerID, token).ConfigureAwait(false),
            PokeTradeType.Clone => await HandleClone(sav, poke, offered, oldEC, token).ConfigureAwait(false),
            PokeTradeType.EditReturn => await HandleEditReturn(sav, poke, offered, toSend, token).ConfigureAwait(false),
            _ => (toSend, PokeTradeResult.Success),
        };
    }

    private async Task<(PK9 toSend, PokeTradeResult check)> HandleClone(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, byte[] oldEC, CancellationToken token)
    {
        if (Hub.Config.Discord.ReturnPKMs)
            poke.SendNotification(this, offered, "Here's what you showed me!");

        var la = new LegalityAnalysis(offered);
        if (!la.Valid)
        {
            Log($"Clone request (from {poke.Trainer.TrainerName}) has detected an invalid Pokémon: {GetSpeciesName(offered.Species)}.");
            if (DumpSetting.Dump)
                DumpPokemon(DumpSetting.DumpFolder, "hacked", offered);

            var report = la.Report();
            Log(report);
            poke.SendNotification(this, "This Pokémon is not legal per PKHeX's legality checks. I am forbidden from cloning this. Exiting trade.");
            poke.SendNotification(this, report);

            return (offered, PokeTradeResult.IllegalTrade);
        }

        var clone = offered.Clone();
        if (Hub.Config.Legality.ResetHOMETracker)
            clone.Tracker = 0;

        var cloneSpecies = GetSpeciesName(clone.Species);
        poke.SendNotification(this, $"**Cloned your {cloneSpecies}!**\nNow press B to cancel your offer and trade me a Pokémon you don't want.");
        Log($"Cloned a {cloneSpecies}. Waiting for user to change their Pokémon...");

        // Separate this out from WaitForPokemonChanged since we compare to old EC from original read.
        var partnerFound = await ReadUntilChanged(TradePartnerOfferedOffset, oldEC, 15_000, 0_200, false, true, token).ConfigureAwait(false);
        if (!partnerFound)
        {
            poke.SendNotification(this, "**HEY CHANGE IT NOW OR I AM LEAVING!!!**");
            // They get one more chance.
            partnerFound = await ReadUntilChanged(TradePartnerOfferedOffset, oldEC, 15_000, 0_200, false, true, token).ConfigureAwait(false);
        }

        var pk2 = await ReadUntilPresent(TradePartnerOfferedOffset, 25_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
        if (!partnerFound || pk2 is null || SearchUtil.HashByDetails(pk2) == SearchUtil.HashByDetails(offered))
        {
            Log("Trade partner did not change their Pokémon.");
            return (offered, PokeTradeResult.TrainerTooSlow);
        }

        await Click(A, 0_800, token).ConfigureAwait(false);
        await SetBoxPokemonAbsolute(BoxStartOffset, clone, token, sav).ConfigureAwait(false);

        return (clone, PokeTradeResult.Success);
    }

    private async Task<(PK9 toSend, PokeTradeResult check)> HandleEditReturn(
        SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, PK9 target, CancellationToken token)
    {
        // --- Team mode: resolve target from EditReturnTargets ---
        var targets = poke.EditReturnTargets;
        bool teamMode = targets is { Count: > 0 };
        int teamIdx = -1;
        if (teamMode)
        {
            teamIdx = FindTeamTargetIndex(targets!, offered.Species, offered.Form);
            if (teamIdx < 0)
            {
                var remainingNames = string.Join(", ", targets!.Select(t => GetSpeciesName(t.Species)));
                poke.SendNotification(this,
                    $"Your {GetSpeciesName(offered.Species)} isn't in this request's remaining team. Remaining: {remainingNames}. Offer one of those.");
                return (offered, PokeTradeResult.TrainerRequestBad);
            }
            target = targets![teamIdx];
        }

        // Species/form gate (skip in team mode — matching already guaranteed it)
        if (!teamMode && (offered.Species != target.Species || offered.Form != target.Form))
        {
            poke.SendNotification(this, $"Edit-return keeps your Pokémon's species. You offered {GetSpeciesName(offered.Species)} but the set is for {GetSpeciesName(target.Species)}. Offer the matching species.");
            return (offered, PokeTradeResult.TrainerRequestBad);
        }

        // The offered mon must be legal
        var la = new LegalityAnalysis(offered);
        if (!la.Valid)
        {
            Log($"Edit-return request (from {poke.Trainer.TrainerName}) has detected an invalid Pokémon: {GetSpeciesName(offered.Species)}.");
            if (DumpSetting.Dump)
                DumpPokemon(DumpSetting.DumpFolder, "hacked", offered);

            var report = la.Report();
            Log(report);
            poke.SendNotification(this, "The Pokémon you offered isn't legal per PKHeX, so I won't edit it. Exiting trade.");
            return (offered, PokeTradeResult.IllegalTrade);
        }

        // DIAGNOSTIC: capture the exact offered mon so sets can be verified against the real
        // Pokemon (level/met/encounter), not an idealized one.
        try
        {
            var _buf = new byte[offered.SIZE_PARTY];
            offered.WriteDecryptedDataParty(_buf);
            if (EditReturnDebugDump)
                System.IO.File.WriteAllBytes("/opt/sysbot/app/records/editreturn_offered.pk9", _buf);
            Log($"Edit-return offered: {GetSpeciesName(offered.Species)} Lv{offered.CurrentLevel} MetLv{offered.MetLevel} Ball{offered.Ball} Egg{offered.WasEgg} Enc={la.EncounterOriginal?.GetType().Name} Moves={offered.Move1}/{offered.Move2}/{offered.Move3}/{offered.Move4} Relearn={offered.RelearnMove1}/{offered.RelearnMove2}/{offered.RelearnMove3}/{offered.RelearnMove4}");
        }
        catch (Exception ex) { Log($"Edit-return offered dump failed: {ex.Message}"); }

        // Build the edited mon on a clone of the offered mon
        var edited = offered.Clone();

        // Copy only the competitive layer from target
        edited.EV_HP = target.EV_HP;
        edited.EV_ATK = target.EV_ATK;
        edited.EV_DEF = target.EV_DEF;
        edited.EV_SPA = target.EV_SPA;
        edited.EV_SPD = target.EV_SPD;
        edited.EV_SPE = target.EV_SPE;

        // Held item is deliberately not copied: HOME strips items on deposit, so it can
        // never reach Champions -- setting it only adds legality surface.

        edited.Ability = target.Ability;
        edited.AbilityNumber = target.AbilityNumber;

        edited.StatNature = target.StatNature;

        // Tera is provenance, not competitive layer: Champions has no tera mechanic, and
        // HOME-transferred mons have a fixed expected tera that an overwrite breaks.

        // Raise the level to the set's level so the requested moves are legal: a low-level offered
        // mon can't legally know moves learned above its current level (the Move Reminder only
        // re-teaches up to the current level). Met level is unchanged -- training a mon up is always
        // legal -- and we never drop below met level.
        edited.CurrentLevel = (byte)System.Math.Max(target.CurrentLevel, offered.MetLevel);

        edited.SetMoves(new ushort[] { target.Move1, target.Move2, target.Move3, target.Move4 }, true);

        // Gen 9 mons carry per-TM record flags (which TMs were ever used on them -- the Move
        // Reminder reads these). A TM move without its flag is judged Unobtainable, so flag the
        // requested moves; level-up and egg moves ignore the flags.
        edited.SetRecordFlags(new ushort[] { target.Move1, target.Move2, target.Move3, target.Move4 });
        var laRelearn = new LegalityAnalysis(edited);
        Span<ushort> relearn = stackalloc ushort[4];
        laRelearn.GetSuggestedRelearnMoves(relearn);
        edited.SetRelearnMoves(relearn);

        edited.RefreshChecksum();

        // Legality re-check with guarded repair fallback
        var laEdit = new LegalityAnalysis(edited);
        string? preRepairReport = null;
        if (!laEdit.Valid)
        {
            preRepairReport = laEdit.Report();
            Log($"Edit-return pre-repair legality for {poke.Trainer.TrainerName}:\n{preRepairReport}");
            edited = (PK9)edited.LegalizePokemon();
            edited.RefreshChecksum();
            laEdit = new LegalityAnalysis(edited);
        }
        if (!laEdit.Valid)
        {
            Log(laEdit.Report());
            poke.SendNotification(this, "I couldn't make that set legal for your Pokémon (a move or ability may not be obtainable). Exiting trade.");
            poke.SendNotification(this, CondenseLegalityReport(laEdit.Report()));

            // Team-mode bookkeeping: remove this set, mark failed
            if (teamMode)
            {
                targets!.RemoveAt(teamIdx);
                poke.EditReturnFailed.Add($"{(Species)target.Species} — set not legal for your Pokémon");
            }

            return (offered, PokeTradeResult.IllegalTrade);
        }

        // Provenance hard-guard: abort if identity drifted
        if (edited.EncryptionConstant != offered.EncryptionConstant ||
            edited.PID != offered.PID ||
            edited.ID32 != offered.ID32 ||
            edited.OriginalTrainerName != offered.OriginalTrainerName ||
            edited.OriginalTrainerGender != offered.OriginalTrainerGender ||
            edited.Version != offered.Version ||
            edited.Language != offered.Language ||
            edited.MetLocation != offered.MetLocation ||
            edited.MetLevel != offered.MetLevel ||
            edited.EggLocation != offered.EggLocation ||
            edited.Ball != offered.Ball ||
            ((IHomeTrack)edited).Tracker != ((IHomeTrack)offered).Tracker)
        {
            var drift = new List<string>();
            if (edited.EncryptionConstant != offered.EncryptionConstant) drift.Add("EncryptionConstant");
            if (edited.PID != offered.PID) drift.Add("PID");
            if (edited.ID32 != offered.ID32) drift.Add("ID32");
            if (edited.OriginalTrainerName != offered.OriginalTrainerName) drift.Add("OT");
            if (edited.OriginalTrainerGender != offered.OriginalTrainerGender) drift.Add("OTGender");
            if (edited.Version != offered.Version) drift.Add("Version");
            if (edited.Language != offered.Language) drift.Add("Language");
            if (edited.MetLocation != offered.MetLocation) drift.Add("MetLocation");
            if (edited.MetLevel != offered.MetLevel) drift.Add("MetLevel");
            if (edited.EggLocation != offered.EggLocation) drift.Add("EggLocation");
            if (edited.Ball != offered.Ball) drift.Add("Ball");
            if (((IHomeTrack)edited).Tracker != ((IHomeTrack)offered).Tracker) drift.Add("Tracker");
            Log($"Edit-return provenance guard triggered for {poke.Trainer.TrainerName}. Drifted: {string.Join(", ", drift)}");

            // Team-mode bookkeeping: remove this set, mark failed
            if (teamMode)
            {
                targets!.RemoveAt(teamIdx);
                poke.EditReturnFailed.Add($"{(Species)target.Species} — set not legal for your Pokémon");
            }

            if (preRepairReport != null)
            {
                poke.SendNotification(this,
                    $"That set isn't legal for the Pokémon you offered — its level, origin, or moves don't allow it, so I didn't alter it. PKHeX says:\n{CondenseLegalityReport(preRepairReport)}");
            }
            else
            {
                poke.SendNotification(this,
                    "Safety check failed — I did not alter your Pokémon. This looks like a bot problem, not your set; tell the commissioner.");
            }

            return (offered, PokeTradeResult.IllegalTrade);
        }

        // Success: inject the edited mon and return
        poke.SendNotification(this, $"**Editing your {GetSpeciesName(edited.Species)}** with the requested set — trading it back now.");
        Log($"Edit-return: applied set to {GetSpeciesName(edited.Species)} for {poke.Trainer.TrainerName}.");

        // Team-mode bookkeeping: mark done, remove from targets
        if (teamMode)
        {
            targets!.RemoveAt(teamIdx);
            poke.EditReturnDone.Add(GetSpeciesName(target.Species));
        }

        await Click(A, 0_800, token).ConfigureAwait(false);
        await SetBoxPokemonAbsolute(BoxStartOffset, edited, token, sav).ConfigureAwait(false);

        return (edited, PokeTradeResult.Success);
    }

    private async Task<(PK9 toSend, PokeTradeResult check)> HandleRandomLedy(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, PK9 toSend, PartnerDataHolder partner, CancellationToken token)
    {
        // Allow the trade partner to do a Ledy swap.
        var config = Hub.Config.Distribution;
        var trade = Hub.Ledy.GetLedyTrade(offered, partner.TrainerOnlineID, config.LedySpecies);
        if (trade != null)
        {
            if (trade.Type == LedyResponseType.AbuseDetected)
            {
                var msg = $"Found {partner.TrainerName} has been detected for abusing Ledy trades.";
                if (AbuseSettings.EchoNintendoOnlineIDLedy)
                    msg += $"\nID: {partner.TrainerOnlineID}";
                if (!string.IsNullOrWhiteSpace(AbuseSettings.LedyAbuseEchoMention))
                    msg = $"{AbuseSettings.LedyAbuseEchoMention} {msg}";
                EchoUtil.Echo(msg);

                return (toSend, PokeTradeResult.SuspiciousActivity);
            }

            toSend = trade.Receive;
            poke.TradeData = toSend;

            poke.SendNotification(this, "Injecting the requested Pokémon.");
            await SetBoxPokemonAbsolute(BoxStartOffset, toSend, token, sav).ConfigureAwait(false);
        }
        else if (config.LedyQuitIfNoMatch)
        {
            var nickname = offered.IsNicknamed ? $" (Nickname: \"{offered.Nickname}\")" : string.Empty;
            poke.SendNotification(this, $"No match found for the offered {GetSpeciesName(offered.Species)}{nickname}.");
            return (toSend, PokeTradeResult.TrainerRequestBad);
        }

        return (toSend, PokeTradeResult.Success);
    }

    private void WaitAtBarrierIfApplicable(CancellationToken token)
    {
        if (!ShouldWaitAtBarrier)
            return;
        var opt = Hub.Config.Distribution.SynchronizeBots;
        if (opt == BotSyncOption.NoSync)
            return;

        var timeoutAfter = Hub.Config.Distribution.SynchronizeTimeout;
        if (FailedBarrier == 1) // failed last iteration
            timeoutAfter *= 2; // try to re-sync in the event things are too slow.

        var result = Hub.BotSync.Barrier.SignalAndWait(TimeSpan.FromSeconds(timeoutAfter), token);

        if (result)
        {
            FailedBarrier = 0;
            return;
        }

        FailedBarrier++;
        Log($"Barrier sync timed out after {timeoutAfter} seconds. Continuing.");
    }

    /// <summary>
    /// Checks if the barrier needs to get updated to consider this bot.
    /// If it should be considered, it adds it to the barrier if it is not already added.
    /// If it should not be considered, it removes it from the barrier if not already removed.
    /// </summary>
    private void UpdateBarrier(bool shouldWait)
    {
        if (ShouldWaitAtBarrier == shouldWait)
            return; // no change required

        ShouldWaitAtBarrier = shouldWait;
        if (shouldWait)
        {
            Hub.BotSync.Barrier.AddParticipant();
            Log($"Joined the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
        }
        else
        {
            Hub.BotSync.Barrier.RemoveParticipant();
            Log($"Left the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
        }
    }

    /// <summary>
    /// Find first index in targets matching species+form. Returns -1 if not found.
    /// </summary>
    public static int FindTeamTargetIndex(IReadOnlyList<PK9> targets, ushort species, byte form)
    {
        for (int i = 0; i < targets.Count; i++)
            if (targets[i].Species == species && targets[i].Form == form)
                return i;
        return -1;
    }

    /// <summary>
    /// Condense a PKHeX legality report: keep lines containing "Invalid" (case-sensitive),
    /// or first 3 lines if none match. Truncate to 900 chars.
    /// </summary>
    private static string CondenseLegalityReport(string report)
    {
        var lines = report.Split('\n');
        var invalidLines = lines.Where(l => l.Contains("Invalid")).ToList();
        var selected = invalidLines.Count > 0 ? invalidLines : lines.Take(3).ToList();
        var result = string.Join("\n", selected);
        return result.Length > 900 ? result.Substring(0, 900) : result;
    }

    /// <summary>
    /// Determine whether to continue a team edit-return session.
    /// Continues on Success, IllegalTrade, TrainerRequestBad, TrainerTooSlow with remaining > 0 and attempts < cap.
    /// Stops on remaining == 0, attempts >= cap, or non-continuable results (NoTrainerFound, RecoverStart, etc.).
    /// </summary>
    public static bool ShouldContinueEditReturnSession(int remaining, int attempts, int cap, PokeTradeResult result)
    {
        if (remaining <= 0) return false;
        if (attempts >= cap) return false;
        return result is PokeTradeResult.Success
            or PokeTradeResult.IllegalTrade
            or PokeTradeResult.TrainerRequestBad
            or PokeTradeResult.TrainerTooSlow;
    }
}
