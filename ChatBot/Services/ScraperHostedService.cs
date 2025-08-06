using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        private readonly string _pythonScriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "scraper.py");
        private readonly string _pythonConfigPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "config.json");


        public ScraperHostedService(ILogger<ScraperHostedService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _scrapingIntervalDays = _configuration.GetValue<int>("ScraperSettings:ScrapingIntervalDays", 7);

            // Validate Python script and config
            if (!File.Exists(_pythonScriptPath))
            {
                throw new FileNotFoundException($"Python script not found at {_pythonScriptPath}");
            }
            if (!File.Exists(_pythonConfigPath))
            {
                throw new FileNotFoundException($"Python config not found at {_pythonConfigPath}");
            }

            InitializeFromCache();
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
                _logger.LogInformation("Starting Python scraper process");

                bool scrapingSuccessful = true;
                string newContent = string.Empty;

                try
                {
                    var scriptFullPath = Path.GetFullPath(_pythonScriptPath);
                    var configFullPath = Path.GetFullPath(_pythonConfigPath);

                    var processInfo = new ProcessStartInfo
                    {
                        FileName = @"C:\Users\INBOX001\python.exe",
                        Arguments = $"\"{scriptFullPath}\" \"{configFullPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };


                    using var process = new Process { StartInfo = processInfo };
                    process.Start();

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync(stoppingToken);

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("Python scraper failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
                        scrapingSuccessful = false;
                    }
                    else
                    {
                        _logger.LogInformation("Python scraper executed successfully");
                        // Read output from scraped_content.txt
                        if (File.Exists(_cacheFilePath))
                        {
                            newContent = File.ReadAllText(_cacheFilePath);
                        }
                        else
                        {
                            _logger.LogWarning("Python scraper did not generate output file at {CacheFilePath}", _cacheFilePath);
                            scrapingSuccessful = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to execute Python scraper");
                    scrapingSuccessful = false;
                }

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
                        _logger.LogWarning("Scraping failed. Attempting to load content from cache.");
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