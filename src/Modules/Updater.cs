using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.Storage;

namespace Texture_Set_Manager.Modules;

/// =====================================================================================================================
/// Silently tries to update the credits from Vanilla RTX's readme -- any failure will result in null.
/// Cooldowns also result in null, check for null and don't show credits whereever this class is used.
/// =====================================================================================================================

public class CreditsUpdater
{
    private const string CREDITS_CACHE_KEY = "CreditsCache";
    private const string CREDITS_TIMESTAMP_KEY = "CreditsTimestamp";
    private const string CREDITS_LAST_SHOWN_KEY = "CreditsLastShown";
    private const string README_URL = "https://raw.githubusercontent.com/Cubeir/Vanilla-RTX/master/README.md";
    private const int CACHE_UPDATE_COOLDOWN_DAYS = 1;
    private const int DISPLAY_COOLDOWN_DAYS = 0;

    public static string Credits { get; private set; } = string.Empty;
    private static readonly object _updateLock = new();
    private static bool _isUpdating = false;

    public static string GetCredits(bool returnString = false)
    {
        try
        {
            var updater = new CreditsUpdater();
            var cachedCredits = updater.GetCachedCredits();

            // If no cache or update cooldown expired, trigger background update (only one at a time)
            if ((string.IsNullOrEmpty(cachedCredits) || updater.ShouldUpdateCache()) && !_isUpdating)
            {
                lock (_updateLock)
                {
                    if (!_isUpdating) // Double-check inside lock
                    {
                        _isUpdating = true;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // MainWindow.Instance?.BlinkingLamp(true);
                                var freshCredits = await updater.FetchAndExtractCreditsAsync();
                                if (!string.IsNullOrEmpty(freshCredits))
                                {
                                    Credits = freshCredits;
                                    updater.CacheCredits(freshCredits);
                                }
                            }
                            catch
                            {
                                // Silently fail background update
                            }
                            finally
                            {
                                // MainWindow.Instance?.BlinkingLamp(false);
                                lock (_updateLock)
                                {
                                    _isUpdating = false;
                                }
                            }
                        });
                    }
                }
            }

            // Check display cooldown - return null if still in cooldown period
            if (!updater.ShouldShowCredits())
            {
                return null;
            }

            // Update last shown timestamp when credits are about to be displayed
            if (!string.IsNullOrEmpty(cachedCredits))
            {
                updater.UpdateLastShownTimestamp();
            }

            // Only return credits if display is allowed AND cache exists
            return returnString && !string.IsNullOrEmpty(cachedCredits) ? cachedCredits : null;
        }
        catch
        {
            return null;
        }
    }

    private string? GetCachedCredits()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            return localSettings.Values.TryGetValue(CREDITS_CACHE_KEY, out var value)
                ? value.ToString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ShouldUpdateCache()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;

            if (!localSettings.Values.TryGetValue(CREDITS_TIMESTAMP_KEY, out var value))
                return true;

            if (DateTime.TryParse(value.ToString(), out var cachedTime))
            {
                return DateTime.Now - cachedTime >= TimeSpan.FromDays(CACHE_UPDATE_COOLDOWN_DAYS);
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    private bool ShouldShowCredits()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;

            if (!localSettings.Values.TryGetValue(CREDITS_LAST_SHOWN_KEY, out var value))
                return true; // Never shown before, allow showing

            if (DateTime.TryParse(value.ToString(), out var lastShownTime))
            {
                return DateTime.Now - lastShownTime >= TimeSpan.FromDays(DISPLAY_COOLDOWN_DAYS);
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    private void UpdateLastShownTimestamp()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            localSettings.Values[CREDITS_LAST_SHOWN_KEY] = DateTime.Now.ToString();
        }
        catch
        {
            // Silently ignore timestamp update failures
        }
    }

    private async Task<string> FetchAndExtractCreditsAsync()
    {
        try
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                string userAgent = $"Texture_Set_Manager_updater/{EnvironmentVariables.appVersion} (https://github.com/Cubeir/Vanilla-RTX-App)";
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);

                var response = await client.GetAsync(README_URL);
                if (!response.IsSuccessStatusCode)
                    return null;

                var content = await response.Content.ReadAsStringAsync();

                // Extract credits between "### Credits" and "——"
                int creditsIndex = content.IndexOf("### Credits", StringComparison.OrdinalIgnoreCase);
                if (creditsIndex == -1)
                    return null;

                string afterCredits = content.Substring(creditsIndex + "### Credits".Length).Trim();
                int delimiterIndex = afterCredits.IndexOf("——");
                if (delimiterIndex == -1)
                    return null;

                return afterCredits.Substring(0, delimiterIndex).Trim() +
                       "\n\nConsider supporting development of my projects, maybe you'll find your name here next time!? ❤️";
            }
        }
        catch
        {
            return null;
        }
    }

    private void CacheCredits(string credits)
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            localSettings.Values[CREDITS_CACHE_KEY] = credits;
            localSettings.Values[CREDITS_TIMESTAMP_KEY] = DateTime.Now.ToString();
        }
        catch
        {
            // Silent fails
        }
    }

    public static void ForceUpdateCache()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            localSettings.Values[CREDITS_TIMESTAMP_KEY] = DateTime.Now.AddDays(-10).ToString();
            localSettings.Values[CREDITS_LAST_SHOWN_KEY] = DateTime.Now.AddDays(-10).ToString();
        }
        catch
        {
        }
    }
}

/// =====================================================================================================================
/// Show PSA from github readme, simply add a ### PSA tag followed by the announcement at the end of the readme file linked below
/// =====================================================================================================================

public class PSAUpdater
{
    private const string README_URL = "https://raw.githubusercontent.com/Cubeir/Texture-Set-Manager/main/README.md";
    private const string CACHE_KEY = "PSAContentCache";
    private const string TIMESTAMP_KEY = "PSALastCheckedTimestamp";
    private const string LAST_SHOWN_KEY = "PSALastShownTimestamp";
    private static readonly TimeSpan COOLDOWN = TimeSpan.FromHours(6);
    private static readonly TimeSpan SHOW_COOLDOWN = TimeSpan.FromMinutes(1);

    public static async Task<string?> GetPSAAsync()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;

            // Check if we need to fetch new data from GitHub
            bool shouldFetch = true;
            if (localSettings.Values.ContainsKey(TIMESTAMP_KEY))
            {
                var lastChecked = DateTime.Parse(localSettings.Values[TIMESTAMP_KEY] as string);
                if (DateTime.UtcNow - lastChecked < COOLDOWN)
                {
                    shouldFetch = false;
                }
            }

            // Fetch new data if cooldown expired
            if (shouldFetch)
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    var userAgent = $"Texture_Set_Manager_updater/{EnvironmentVariables.appVersion} (https://github.com/Cubeir/Vanilla-RTX-App)";
                    client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                    var response = await client.GetAsync(README_URL);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        int psaIndex = content.IndexOf("### PSA", StringComparison.OrdinalIgnoreCase);

                        if (psaIndex != -1)
                        {
                            var afterPSA = content.Substring(psaIndex + "### PSA".Length).Trim();
                            var result = string.IsNullOrWhiteSpace(afterPSA) ? null : afterPSA;

                            // Cache the result and update fetch timestamp
                            localSettings.Values[CACHE_KEY] = result;
                        }

                        localSettings.Values[TIMESTAMP_KEY] = DateTime.UtcNow.ToString("O");
                    }
                }
            }

            // Now check if we should show the PSA based on last shown time
            if (localSettings.Values.ContainsKey(LAST_SHOWN_KEY))
            {
                var lastShown = DateTime.Parse(localSettings.Values[LAST_SHOWN_KEY] as string);
                if (DateTime.UtcNow - lastShown < SHOW_COOLDOWN)
                {
                    // Too soon to show again
                    return null;
                }
            }

            // Get cached content to show
            var cachedContent = localSettings.Values.ContainsKey(CACHE_KEY)
                ? localSettings.Values[CACHE_KEY] as string
                : null;

            // Update last shown timestamp if we have content to show
            if (!string.IsNullOrWhiteSpace(cachedContent))
            {
                localSettings.Values[LAST_SHOWN_KEY] = DateTime.UtcNow.ToString("O");
            }

            return cachedContent;
        }
        catch
        {
            // On error, check if we can still show cached content
            var localSettings = ApplicationData.Current.LocalSettings;

            // Check show cooldown
            if (localSettings.Values.ContainsKey(LAST_SHOWN_KEY))
            {
                var lastShown = DateTime.Parse(localSettings.Values[LAST_SHOWN_KEY] as string);
                if (DateTime.UtcNow - lastShown < SHOW_COOLDOWN)
                {
                    return null;
                }
            }

            var cachedContent = localSettings.Values.ContainsKey(CACHE_KEY)
                ? localSettings.Values[CACHE_KEY] as string
                : null;

            if (!string.IsNullOrWhiteSpace(cachedContent))
            {
                localSettings.Values[LAST_SHOWN_KEY] = DateTime.UtcNow.ToString("O");
            }

            return cachedContent;
        }
    }
}
