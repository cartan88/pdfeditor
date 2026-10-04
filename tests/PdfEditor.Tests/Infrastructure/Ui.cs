using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PdfEditor.Tests.Infrastructure;

/// <summary>
/// Runs test code on one long-lived STA thread with a WPF dispatcher, which every WPF object needs.
/// A plain <see cref="Application"/> carries the app's styles from App.xaml; <c>PdfEditor.App</c> itself is never
/// created, because its startup opens a real window. Tests never show windows: controls are laid out in memory.
/// </summary>
internal static class Ui
{
    private static readonly Lazy<Dispatcher> UiDispatcher = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task Run(Action body) => UiDispatcher.Value.InvokeAsync(body).Task;

    public static Task<T> Run<T>(Func<T> body) => UiDispatcher.Value.InvokeAsync(body).Task;

    public static Task Run(Func<Task> body) => UiDispatcher.Value.InvokeAsync(body).Task.Unwrap();

    public static Task<T> Run<T>(Func<Task<T>> body) => UiDispatcher.Value.InvokeAsync(body).Task.Unwrap();

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources = LoadAppResources();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            ready.Set();
            if (failure == null) Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test UI thread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (failure != null) throw new InvalidOperationException("Could not start the WPF test thread.", failure);
        AppDomain.CurrentDomain.ProcessExit += (s, e) => dispatcher!.InvokeShutdown();
        return dispatcher!;
    }

    /// <summary>The &lt;Application.Resources&gt; of the app's App.xaml, as a ResourceDictionary.</summary>
    private static ResourceDictionary LoadAppResources()
    {
        var xaml = File.ReadAllText(Path.Combine(TestData.Dir, "App.xaml"));
        var inner = Regex.Match(xaml, @"<Application\.Resources>(.*)</Application\.Resources>", RegexOptions.Singleline).Groups[1].Value;
        return (ResourceDictionary)XamlReader.Parse(
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + inner + "</ResourceDictionary>");
    }
}
