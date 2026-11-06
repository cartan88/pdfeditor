using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace PdfEditor.Services;

/// <summary>
/// Self-updating from the GitHub releases, using Velopack. Only copies installed with the Setup program can update;
/// the zip and development builds never contact GitHub (<see cref="IsInstalled"/> is false).
/// </summary>
public sealed class AppUpdater
{
    public const string ReleasesPage = "https://github.com/cartan88/pdfeditor/releases";
    private const string Repository = "https://github.com/cartan88/pdfeditor";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _ready;

    private AppUpdater(UpdateManager? manager) => _manager = manager;

    /// <summary>An updater that never does anything (tests, and copies that weren't installed).</summary>
    public static AppUpdater Disabled { get; } = new(null);

    /// <summary>The updater for the running app: active only when it was installed with the Setup program.</summary>
    public static AppUpdater ForThisProcess()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(Repository, null, false));
            return new AppUpdater(manager.IsInstalled ? manager : null);
        }
        catch (Exception)
        {
            return Disabled;
        }
    }

    public bool IsInstalled => _manager != null;

    /// <summary>The running version, e.g. "1.3.6".</summary>
    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>Version of a downloaded update waiting to be applied, or null.</summary>
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>
    /// Checks GitHub for a newer release and downloads it in the background. Returns the new version once it is
    /// ready to apply, or null if there is none (or this copy can't update). Throws on network errors.
    /// </summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (_manager == null) return null;
        if (_ready != null) return ReadyVersion;
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info == null) return null;
        await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
        _ready = info;
        return ReadyVersion;
    }

    /// <summary>Closes the app, applies the downloaded update and starts the new version with <paramref name="args"/>.</summary>
    public void RestartToUpdate(string[] args)
    {
        if (_manager != null && _ready != null) _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease, args);
    }

    /// <summary>Applies the downloaded update after the app has closed, without restarting it.</summary>
    public void ApplyWhenClosed()
    {
        if (_manager != null && _ready != null) _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: false);
    }
}
