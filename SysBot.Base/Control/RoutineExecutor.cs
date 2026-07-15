using System;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Base;

/// <summary>
/// Commands a Bot to a perform a routine asynchronously.
/// </summary>
public abstract class RoutineExecutor<T>(IConsoleBotManaged<IConsoleConnection, IConsoleConnectionAsync> Config)
    : IRoutineExecutor
    where T : class, IConsoleBotConfig
{
    public readonly IConsoleConnectionAsync Connection = Config.CreateAsynchronous();
    public readonly T Config = (T)Config;

    public string LastLogged { get; private set; } = "Not Started";
    public DateTime LastTime { get; private set; } = DateTime.Now;

    public void ReportStatus() => LastTime = DateTime.Now;

    public abstract string GetSummary();

    public void Log(string message)
    {
        Connection.Log(message);
        LastLogged = message;
        LastTime = DateTime.Now;
    }

    /// <summary>
    /// Connects to the console, then runs the bot.
    /// </summary>
    /// <param name="token">Cancel this token to have the bot stop looping.</param>
    public async Task RunAsync(CancellationToken token)
    {
        // Connectivity loss must never permanently end the bot; only a stop request (token cancellation) does.
        while (!token.IsCancellationRequested)
        {
            // Connect phase: retry until connected or cancelled.
            while (true)
            {
                try
                {
                    Connection.Connect();
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Initial connection failed: {ex.Message} Retrying in 30 seconds...");
                    try
                    {
                        await Task.Delay(30_000, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                if (token.IsCancellationRequested)
                    break;
            }
            if (token.IsCancellationRequested)
                break;

            // Session phase: run InitialStartup then MainLoop.
            try
            {
                Log("Initializing connection with console...");
                await InitialStartup(token).ConfigureAwait(false);
                await MainLoop(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Bot session faulted: {ex}");
            }

            // After the session phase: if we were told to stop, exit. Otherwise, reinitialize.
            if (token.IsCancellationRequested)
                break;

            Log("Bot session ended without a stop request; reinitializing in 30 seconds...");

            // Best-effort disconnect so the next connect phase starts from a fresh socket.
            try
            {
                Connection.Disconnect();
            }
            catch (Exception ex)
            {
                Log($"Disconnect during reinitialization failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(30_000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Final best-effort disconnect.
        try
        {
            Connection.Disconnect();
        }
        catch (Exception ex)
        {
            Log($"Final disconnect failed: {ex.Message}");
        }
    }

    public abstract Task MainLoop(CancellationToken token);
    public abstract Task InitialStartup(CancellationToken token);
    public abstract void SoftStop();
    public abstract Task HardStop();
}
