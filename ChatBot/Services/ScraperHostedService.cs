using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ChatBot.Services
{
    public class ScraperHostedService : BackgroundService
    {
        public static string LatestWebsiteContent = "";
        private static readonly object _contentLock = new object();
        private readonly ILogger<ScraperHostedService> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _cacheFilePath = Path.Combine("wwwroot", "scraped_content.txt");
        private readonly int _scrapingIntervalDays;
        private readonly string[] _urlsToScrape;
        private readonly Dictionary<string, int> _jobOpeningsStatus;
        private readonly Dictionary<string, List<string>> _sectionMappings;

        public ScraperHostedService(ILogger<ScraperHostedService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _scrapingIntervalDays = _configuration.GetValue<int>("ScraperSettings:ScrapingIntervalDays", 7);
            _urlsToScrape = _configuration.GetSection("ScraperSettings:UrlsToScrape").Get<string[]>();
            _jobOpeningsStatus = _configuration.GetSection("JobOpeningsStatus").Get<Dictionary<string, int>>() ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Load section mappings
            var clientKey = GetClientKeyFromUrl(_urlsToScrape.FirstOrDefault());
            _sectionMappings = _configuration.GetSection($"SectionMappings:{clientKey}")
                .Get<Dictionary<string, List<string>>>() ?? new Dictionary<string, List<string>>();

            if (_sectionMappings.Count == 0)
            {
                _logger.LogWarning("No section mappings found for {ClientKey}. Using default mappings.", clientKey);
                _sectionMappings = new Dictionary<string, List<string>>
                {
                    { "About", new List<string> { "About", "About Us", "Who We Are", "Our Story" } },
                    { "Services", new List<string> { "Services", "Our Services", "What We Offer", "Solutions" } },
                    { "Products", new List<string> { "Products", "Our Products", "Solutions" } },
                    { "Jobs", new List<string> { "Careers", "Job Openings", "Join Us", "Jobs" } },
                    { "Contact", new List<string> { "Contact", "Contact Us", "Get in Touch" } },
                    { "Industries", new List<string> { "Industries", "Sectors", "Markets" } },
                    { "Awards", new List<string> { "Awards", "Achievements", "Recognitions" } }
                };
            }

            if (_urlsToScrape == null || _urlsToScrape.Length == 0 || _urlsToScrape.All(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException("ScraperSettings:UrlsToScrape is missing, empty, or contains invalid URLs in configuration.");
            }

            InitializeFromCache();
        }

        private string GetClientKeyFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "Default";
            var host = new Uri(url).Host;
            return host.Split('.')[0]; // e.g., "inboxtechs" from "inboxtechs.com"
        }

        private void InitializeFromCache()
        {
            lock (_contentLock)
            {
                if (File.Exists(_cacheFilePath))
                {
                    try
                    {
                        LatestWebsiteContent = File.ReadAllText(_cacheFilePath);
                        _logger.LogInformation("Initialized LatestWebsiteContent from cache file: {CacheFilePath}", _cacheFilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to load cache file: {CacheFilePath}", _cacheFilePath);
                    }
                }
                else
                {
                    _logger.LogWarning("No cache file found at {CacheFilePath}. LatestWebsiteContent remains empty.", _cacheFilePath);
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Starting website scraping process for URLs: {Urls}", string.Join(", ", _urlsToScrape));

                var sb = new StringBuilder();
                var uniqueItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Add job openings from appsettings.json
                sb.AppendLine("🔸 JOB OPENINGS:");
                var enabledJobs = _jobOpeningsStatus.Where(kvp => kvp.Value == 1).Select(kvp => kvp.Key).ToList();
                if (enabledJobs.Any())
                {
                    _logger.LogInformation("Using job openings from JobOpeningsStatus: {Jobs}", string.Join(", ", enabledJobs));
                    foreach (var job in enabledJobs)
                    {
                        if (uniqueItems.Add(job))
                            sb.AppendLine($"- {job}");
                    }
                }
                sb.AppendLine();

                bool scrapingSuccessful = true;

                using var playwright = await Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true
                });

                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36",
                    ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
                });

                var page = await context.NewPageAsync();

                foreach (var url in _urlsToScrape)
                {
                    if (string.IsNullOrWhiteSpace(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
                    {
                        _logger.LogWarning("Skipping invalid URL: {Url}", url);
                        sb.AppendLine($"[Invalid URL skipped: {url}]");
                        continue;
                    }

                    string html = string.Empty;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            _logger.LogInformation("Navigating to {Url} (attempt {Attempt})", url, attempt + 1);
                            await page.GotoAsync(url, new PageGotoOptions
                            {
                                WaitUntil = WaitUntilState.DOMContentLoaded,
                                Timeout = 60000
                            });

                            // Wait for dynamic content to load
                            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 20000 });
                            html = await page.ContentAsync();
                            break;
                        }
                        catch (PlaywrightException ex)
                        {
                            if (attempt == 2)
                            {
                                _logger.LogError(ex, "Failed to load {Url} after 3 attempts", url);
                                sb.AppendLine($"[Failed to load {url} after 3 attempts: {ex.Message}]");
                                scrapingSuccessful = false;
                            }
                            else
                            {
                                _logger.LogWarning("Retry due to: {ErrorMessage}", ex.Message);
                                await Task.Delay(3000, stoppingToken);
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(html)) continue;

                    // Dynamic section identification based on section mappings
                    string sectionName = GetSectionNameFromUrl(url) ?? GetSectionNameFromContent(html);
                    sb.AppendLine(ExtractSection(html, sectionName, uniqueItems));
                }

                string newContent = sb.ToString();
                bool contentChanged = false;

                lock (_contentLock)
                {
                    string currentHash = ComputeMD5Hash(LatestWebsiteContent);
                    string newHash = ComputeMD5Hash(newContent);

                    if (currentHash != newHash)
                    {
                        contentChanged = true;
                        _logger.LogInformation("Website content has changed. Updating LatestWebsiteContent.");
                        LatestWebsiteContent = newContent;
                    }
                    else
                    {
                        _logger.LogInformation("No changes detected in website content.");
                    }

                    if (scrapingSuccessful && contentChanged)
                    {
                        try
                        {
                            Directory.CreateDirectory("wwwroot");
                            if (File.Exists(_cacheFilePath))
                            {
                                File.Delete(_cacheFilePath);
                                _logger.LogInformation("Deleted old cache file: {CacheFilePath}", _cacheFilePath);
                            }
                            File.WriteAllText(_cacheFilePath, LatestWebsiteContent);
                            _logger.LogInformation("Wrote new content to cache file: {CacheFilePath}", _cacheFilePath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to update cache file: {CacheFilePath}", _cacheFilePath);
                        }
                    }
                    else if (!scrapingSuccessful)
                    {
                        _logger.LogWarning("Scraping failed for one or more URLs. Attempting to load content from cache.");
                        if (File.Exists(_cacheFilePath))
                        {
                            try
                            {
                                LatestWebsiteContent = File.ReadAllText(_cacheFilePath);
                                _logger.LogInformation("Loaded content from cache file: {CacheFilePath}", _cacheFilePath);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to load cache file: {CacheFilePath}", _cacheFilePath);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("No cache file available. LatestWebsiteContent remains unchanged.");
                        }
                    }
                }

                _logger.LogInformation("Scraping cycle completed. Content length: {ContentLength}", LatestWebsiteContent.Length);

                await Task.Delay(TimeSpan.FromDays(_scrapingIntervalDays), stoppingToken);
            }
        }

        private string GetSectionNameFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var path = new Uri(url).AbsolutePath.ToLower();
            foreach (var section in _sectionMappings)
            {
                foreach (var name in section.Value)
                {
                    if (path.Contains(name.Replace(" ", "-").ToLower()) || path.Contains(name.Replace(" ", "").ToLower()))
                    {
                        return section.Key;
                    }
                }
            }
            return null;
        }

        private string GetSectionNameFromContent(string html)
        {
            var headingMatch = Regex.Match(html, @"<h[1-6][^>]*>(.*?)</h[1-6]>", RegexOptions.IgnoreCase);
            if (headingMatch.Success)
            {
                var heading = Regex.Replace(headingMatch.Groups[1].Value, "<.*?>", "").Trim().ToLower();
                foreach (var section in _sectionMappings)
                {
                    if (section.Value.Any(v => heading.Contains(v.ToLower())))
                    {
                        return section.Key;
                    }
                }
            }
            return "GENERAL CONTENT";
        }

        private string ExtractSection(string html, string sectionName, HashSet<string> uniqueItems)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"🔸 {sectionName.ToUpper()}:");
            var contentMatches = Regex.Matches(html, @"<p[^>]*>(.*?)</p>|<div[^>]*>(.*?)</div>|<h[1-6][^>]*>(.*?)</h[1-6]>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in contentMatches)
            {
                string text = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
                text = Regex.Replace(text, "<.*?>", "").Trim();
                if (text.Length > 20 && text.Length < 1000 &&
                    !text.Contains("Font Awesome", StringComparison.OrdinalIgnoreCase) &&
                    !Regex.IsMatch(text, @"Sign up|© All Rights Reserved|Social Links", RegexOptions.IgnoreCase) &&
                    uniqueItems.Add(text))
                {
                    sb.AppendLine($"- {text}");
                }
            }

            // Special handling for jobs
            if (sectionName.Equals("Jobs", StringComparison.OrdinalIgnoreCase) && !_jobOpeningsStatus.Any())
            {
                var jobCards = Regex.Matches(html, @"<h[1-6][^>]*>(.*?)</h[1-6]>.*?(?:<p[^>]*>(.*?)</p>|<div[^>]*class=['""]*job[^'""]*['""][^>]*>(.*?)</div>)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                foreach (Match match in jobCards)
                {
                    var title = Regex.Replace(match.Groups[1].Value, "<.*?>", "").Trim();
                    var desc = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
                    desc = Regex.Replace(desc, "<.*?>", "").Trim();
                    if (!string.IsNullOrWhiteSpace(title) &&
                        !Regex.IsMatch(title, @"Sign up|Important Links|Social Links|Font Awesome", RegexOptions.IgnoreCase) &&
                        uniqueItems.Add($"{title}: {desc}"))
                    {
                        sb.AppendLine($"🧑‍💻 {title}: {desc}");
                    }
                }
            }

            // Special handling for contacts
            if (sectionName.Equals("Contact", StringComparison.OrdinalIgnoreCase))
            {
                var phones = Regex.Matches(html, @"(?:tel:|\b)(\+?\d{1,3}[\s.-]?\d{8,12})\b", RegexOptions.IgnoreCase);
                var emails = Regex.Matches(html, @"(?:mailto:|\b)([\w-\.]+@[\w-]+\.[\w]{2,4})\b", RegexOptions.IgnoreCase);
                var addresses = Regex.Matches(html, @"<address[^>]*>(.*?)</address>|(?:\d+\s*[-–—]?\s*[A-Za-z0-9\s,.-]+,\s*[A-Za-z\s]+,\s*[A-Za-z\s]+(?:,\s*\d{5,})?)", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                foreach (Match m in phones)
                {
                    var phone = m.Groups[1].Value.Trim();
                    if (phone.Length >= 10 && phone.Length <= 15 && uniqueItems.Add(phone))
                        sb.AppendLine($"📞 {phone}");
                }
                foreach (Match m in emails)
                {
                    var email = m.Groups[1].Value.Trim();
                    if (uniqueItems.Add(email))
                        sb.AppendLine($"✉️ {email}");
                }
                foreach (Match m in addresses)
                {
                    var address = Regex.Replace(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[0].Value, "<.*?>", "").Trim();
                    if (!string.IsNullOrWhiteSpace(address) && uniqueItems.Add(address))
                        sb.AppendLine($"- Address: {address}");
                }
            }

            sb.AppendLine();
            return sb.ToString();
        }

        private string ComputeMD5Hash(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}