using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetingAssistant;

// Foundry Local can only be set up once per app, so both models share this.
static class Foundry
{
    private static Task? _init;
    private static ICatalog? _catalog;

    public static ICatalog Catalog => _catalog ?? throw new InvalidOperationException("Foundry Local is not ready yet.");

    public static Task InitAsync(Action<string> status) => _init ??= InitCoreAsync(status);

    private static async Task InitCoreAsync(Action<string> status)
    {
        status("Starting Foundry Local...");
        var config = new Configuration
        {
            // Same name as the console version, so already-downloaded models are reused.
            AppName = "dotnet-local-ai-live-transcription",
            LogLevel = LogLevel.Information
        };

        await FoundryLocalManager.CreateAsync(config, NullLogger.Instance);
        var manager = FoundryLocalManager.Instance;

        if (manager.DiscoverEps().Length > 0)
        {
            await manager.DownloadAndRegisterEpsAsync((ep, percent) => status($"Setting up {ep}: {percent:F0}%"));
        }

        _catalog = await manager.GetCatalogAsync();
    }

    public static void Shutdown()
    {
        if (_catalog is not null)
        {
            FoundryLocalManager.Instance.Dispose();
        }
    }

    public static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}
