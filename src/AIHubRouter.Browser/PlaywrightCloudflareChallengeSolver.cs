using AIHubRouter.Core;
using Microsoft.Playwright;

namespace AIHubRouter.Browser;

public sealed class PlaywrightCloudflareChallengeSolver : ICloudflareChallengeSolver
{
    private static readonly TimeSpan HeadlessTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan HeadedTimeout = TimeSpan.FromSeconds(90);

    private static readonly string[] VerificationSelectors =
    [
        "input[value=\"Verify you are human\"]",
        "input[value=\"确认您是真人\"]",
        "input[type=\"checkbox\"]",
        "button:has-text(\"Verify you are human\")",
        "button:has-text(\"确认您是真人\")"
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private bool _disposed;

    public async Task<CloudflareChallengeSolution?> SolveAsync(
        Uri origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var playwright = await GetPlaywrightAsync(cancellationToken);

            var headlessSolution = await TrySolveAsync(
                playwright,
                origin,
                headless: true,
                HeadlessTimeout,
                cancellationToken);
            if (headlessSolution is not null)
            {
                return headlessSolution;
            }

            return await TrySolveAsync(
                playwright,
                origin,
                headless: false,
                HeadedTimeout,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            _playwright?.Dispose();
            _playwright = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IPlaywright> GetPlaywrightAsync(CancellationToken cancellationToken)
    {
        if (_playwright is null)
        {
            _playwright = await Playwright.CreateAsync();
        }

        return _playwright;
    }

    private static async Task<CloudflareChallengeSolution?> TrySolveAsync(
        IPlaywright playwright,
        Uri origin,
        bool headless,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        IBrowser? browser = null;
        try
        {
            browser = await LaunchBrowserAsync(playwright, headless, cancellationToken);
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "zh-CN",
                TimezoneId = "Asia/Shanghai",
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
            });
            var page = await context.NewPageAsync();

            var challengeStarted = DateTimeOffset.UtcNow;
            var userAgent = string.Empty;
            var navigated = false;
            while (DateTimeOffset.UtcNow - challengeStarted < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!navigated)
                {
                    try
                    {
                        await page.GotoAsync(origin.ToString(), new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 20_000
                        });
                        navigated = true;
                    }
                    catch (TimeoutException)
                    {
                        // The challenge keeps the page from reaching DOMContentLoaded; keep waiting.
                    }
                }

                if (userAgent.Length == 0)
                {
                    try
                    {
                        userAgent = await page.EvaluateAsync<string>("() => navigator.userAgent");
                    }
                    catch
                    {
                        // Page may be mid-navigation; retry on the next loop.
                    }
                }

                var cookies = await context.CookiesAsync([origin.ToString()]);
                var clearance = cookies.FirstOrDefault(cookie =>
                    cookie.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase));
                if (clearance is not null && !string.IsNullOrWhiteSpace(clearance.Value) &&
                    !string.IsNullOrWhiteSpace(userAgent))
                {
                    var solutionCookies = cookies
                        .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Value) &&
                            !cookie.Name.StartsWith("cf_chl", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
                    return new CloudflareChallengeSolution(userAgent, solutionCookies);
                }

                await TryClickVerificationAsync(page);
                await Task.Delay(750, cancellationToken);
            }

            return null;
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    await browser.CloseAsync();
                }
                catch
                {
                    // Best effort: the browser may already be gone.
                }
            }
        }
    }

    private static async Task<IBrowser> LaunchBrowserAsync(
        IPlaywright playwright,
        bool headless,
        CancellationToken cancellationToken)
    {
        var channels = OperatingSystem.IsWindows()
            ? new[] { "msedge", "chrome" }
            : new[] { "chrome", "msedge" };
        Exception? lastError = null;
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Channel = channel,
                    Headless = headless,
                    Args =
                    [
                        "--no-sandbox",
                        "--disable-blink-features=AutomationControlled"
                    ]
                });
            }
            catch (Exception exception)
            {
                lastError = exception;
            }
        }

        throw new InvalidOperationException(
            $"未找到可用的 {string.Join(" 或 ", channels)} 浏览器：{lastError?.Message}");
    }

    private static async Task TryClickVerificationAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            foreach (var selector in VerificationSelectors)
            {
                try
                {
                    var locator = frame.Locator(selector).First;
                    if (await locator.CountAsync() > 0)
                    {
                        await locator.ClickAsync(new LocatorClickOptions { Timeout = 1_500 });
                        return;
                    }
                }
                catch
                {
                    // Selector is not actionable yet; try the next one.
                }
            }
        }
    }
}