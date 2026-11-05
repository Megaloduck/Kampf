using Avalonia;
using Avalonia.Headless;
using Kampf;
using Kampf.Ui.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Kampf.Ui.Tests;

/// <summary>
/// Boots the real application headlessly. The App class only builds a window
/// when a desktop lifetime exists, which a test run never sets.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
