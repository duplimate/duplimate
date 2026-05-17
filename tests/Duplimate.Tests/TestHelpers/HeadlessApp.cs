using Avalonia;
using Avalonia.Headless;
using Duplimate;

// Register the Avalonia test application Avalonia.Headless.XUnit will spin up.
// One process-wide app keeps the Dispatcher alive across tests; parallelism
// is disabled in the .csproj so tests share it without fighting.
[assembly: AvaloniaTestApplication(typeof(Duplimate.Tests.TestHelpers.HeadlessApp))]

namespace Duplimate.Tests.TestHelpers;

/// <summary>
/// We reuse the real <see cref="App"/> class directly rather than
/// subclassing — that keeps App.axaml's resources loaded and skips the
/// MainWindow creation automatically. Why no MainWindow? Because the real
/// App only creates one under an <see cref="Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime"/>,
/// and Avalonia.Headless installs a <c>HeadlessApplicationLifetime</c>
/// instead — the conditional inside App.OnFrameworkInitializationCompleted
/// short-circuits, so only <see cref="Duplimate.Services.ServiceLocator.InitializeCore"/>
/// and resource setup run.
/// </summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // Real Skia rasterizer so screenshot tests can produce
                // PNGs. The Avalonia.Skia package is a dependency; calling
                // .UseSkia() AFTER .UseHeadless() keeps the initialization
                // order Headless wants (windowing first, drawing second).
                UseHeadlessDrawing = false,
            })
            .UseSkia();
}
