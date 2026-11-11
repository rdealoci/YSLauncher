using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace YSLauncher.Services;
    public record LanguageItem(
        string Code,
        string CountryCode,
        string NativeName,
        string DisplayName,
        string FallbackInitials
    );

    public static class FlagService
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private static readonly ConcurrentDictionary<string, Bitmap> _cache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string _cacheDir = LauncherConfig.FlagsDir;

        public static readonly LanguageItem[] Top12Languages = new[]
        {
            new LanguageItem("en", "us", "English", "English", "EN"),
            new LanguageItem("es", "es", "Español", "Español", "ES"),
            new LanguageItem("pt", "br", "Português", "Português", "PT"),
            new LanguageItem("tr", "tr", "Türkçe", "Türkçe", "TR"),
            new LanguageItem("ru", "ru", "Русский", "Русский", "RU"),
            new LanguageItem("fr", "fr", "Français", "Français", "FR"),
            new LanguageItem("th", "th", "ไทย", "ไทย", "TH"),
            new LanguageItem("de", "de", "Deutsch", "Deutsch", "DE"),
            new LanguageItem("it", "it", "Italiano", "Italiano", "IT"),
            new LanguageItem("id", "id", "Bahasa Indonesia", "Bahasa Indonesia", "ID"),
            new LanguageItem("pl", "pl", "Polski", "Polski", "PL"),
            new LanguageItem("ja", "jp", "日本語", "日本語", "JA")
        };

        public static string GetCountryCodeForLanguage(string langCode)
        {
            return langCode.ToLowerInvariant() switch
            {
                "en" => "us",
                "pt" => "br",
                "es" => "es",
                "tr" => "tr",
                "ru" => "ru",
                "fr" => "fr",
                "th" => "th",
                "de" => "de",
                "it" => "it",
                "id" => "id",
                "pl" => "pl",
                "ja" => "jp",
                "ko" => "kr",
                "zh" => "cn",
                _ => "us"
            };
        }

        public static string GetFallbackInitials(string langCode)
        {
            return langCode.ToUpperInvariant();
        }

        public static async Task<Bitmap?> LoadFlagAsync(string countryCode)
        {
            countryCode = countryCode.ToLowerInvariant();

            if (_cache.TryGetValue(countryCode, out var memoryCached))
            {
                return memoryCached;
            }

            try
            {
                Directory.CreateDirectory(_cacheDir);
                string localPath = Path.Combine(_cacheDir, $"{countryCode}.png");

                if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                {
                    try
                    {
                        using var fs = File.OpenRead(localPath);
                        var bmp = new Bitmap(fs);
                        _cache[countryCode] = bmp;
                        return bmp;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("Flags", $"Cached flag '{countryCode}' is invalid.", ex);
                        try
                        {
                            File.Delete(localPath);
                        }
                        catch (Exception deleteException)
                        {
                            AppLogger.Warning("Flags", $"Could not delete invalid cached flag '{localPath}'.", deleteException);
                        }
                    }
                }

                string url = $"https://flagcdn.com/w160/{countryCode}.png";
                using var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                    if (bytes != null && bytes.Length > 0)
                    {
                        try
                        {
                            await File.WriteAllBytesAsync(localPath, bytes);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warning("Flags", $"Could not cache flag '{countryCode}'.", ex);
                        }

                        using var ms = new MemoryStream(bytes);
                        var bmp = new Bitmap(ms);
                        _cache[countryCode] = bmp;
                        return bmp;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Flags", $"Could not load flag '{countryCode}'.", ex);
            }

            return null;
        }
    }
