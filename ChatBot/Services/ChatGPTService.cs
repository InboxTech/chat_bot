using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace ChatBot.Services
{
    public class ChatGPTService
    {
        private readonly string _openAIApiKey;
        private readonly string _geminiApiKey;
        private readonly ILogger<ChatGPTService> _logger;
        private readonly Dictionary<string, int> _jobOpeningsStatus;
        private readonly Dictionary<string, List<string>> _sectionMappings;
        private readonly Dictionary<string, string> _responseCache;
        private readonly List<string> _fallbackQuestions;
        private readonly string _interviewQuestionSource;
        private readonly int _interviewQuestionCount;
        private readonly Dictionary<string, List<string>> _manualInterviewQuestions;

        public ChatGPTService(IConfiguration config, ILogger<ChatGPTService> logger)
        {
            _openAIApiKey = config["OpenAI:ApiKey"];
            _geminiApiKey = config["Gemini:ApiKey"];
            _logger = logger;
            _jobOpeningsStatus = new Dictionary<string, int>(
                config.GetSection("JobOpeningsStatus")
                    .Get<Dictionary<string, int>>() ?? new Dictionary<string, int>(),
                StringComparer.OrdinalIgnoreCase
            );
            _responseCache = new Dictionary<string, string>();
            _fallbackQuestions = config.GetSection("FallbackQuestions").Get<List<string>>() ?? new List<string>();
            _interviewQuestionSource = config["InterviewSettings:QuestionSource"]?.ToLower() ?? "ai";
            _interviewQuestionCount = config.GetValue<int>("InterviewSettings:QuestionCount", 5);
            _manualInterviewQuestions = config.GetSection("ManualInterviewQuestions")
                .Get<Dictionary<string, List<string>>>() ?? new Dictionary<string, List<string>>();

            // Validate InterviewSettings
            if (_interviewQuestionSource != "ai" && _interviewQuestionSource != "manual")
            {
                _logger.LogWarning("Invalid InterviewSettings:QuestionSource '{QuestionSource}'. Defaulting to 'AI'.", _interviewQuestionSource);
                _interviewQuestionSource = "ai";
            }
            if (_interviewQuestionCount < 1)
            {
                _logger.LogWarning("Invalid InterviewSettings:QuestionCount '{QuestionCount}'. Defaulting to 5.", _interviewQuestionCount);
                _interviewQuestionCount = 5;
            }

            // Load section mappings
            var clientKey = GetClientKeyFromUrl(config.GetSection("ScraperSettings:UrlsToScrape").Get<string[]>().FirstOrDefault());
            _sectionMappings = config.GetSection($"SectionMappings:{clientKey}")
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

            if (_jobOpeningsStatus.Count == 0)
            {
                _logger.LogWarning("JobOpeningsStatus section is missing or empty in appsettings.json.");
            }
            _logger.LogInformation($"Loaded JobOpeningsStatus: {JsonConvert.SerializeObject(_jobOpeningsStatus)}");
            _logger.LogInformation($"Loaded SectionMappings for {clientKey}: {JsonConvert.SerializeObject(_sectionMappings)}");
            _logger.LogInformation($"Loaded FallbackQuestions: {JsonConvert.SerializeObject(_fallbackQuestions)}");
            _logger.LogInformation($"InterviewSettings: Source={_interviewQuestionSource}, Count={_interviewQuestionCount}");
            _logger.LogInformation($"Loaded ManualInterviewQuestions: {JsonConvert.SerializeObject(_manualInterviewQuestions)}");
        }

        private string GetClientKeyFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "Default";
            var host = new Uri(url).Host;
            return host.Split('.')[0]; // e.g., "inboxtechs" from "inboxtechs.com"
        }

        private string GetSystemPrompt()
        {
            var sectionInstructions = new StringBuilder();
            foreach (var section in _sectionMappings)
            {
                sectionInstructions.AppendLine($"- For {section.Key.ToLower()}-related queries, use the '🔸 {section.Key.ToUpper()}:' section.");
            }

            return $@"
You are a helpful and accurate chatbot for the company whose website content is provided below.
Use ONLY the content between === markers to answer user queries or generate interview questions.
{sectionInstructions}
- If the requested information is not present, say: 'I couldn’t find that information. Try asking about our services, products, location, industries, awards, or job openings.'
- Do not assume or generate information beyond the provided content.

=== WEBSITE CONTENT START ===
{ScraperHostedService.LatestWebsiteContent}
=== WEBSITE CONTENT END ===
";
        }

        public async Task<string> GetResponseAsync(string userMessage)
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _openAIApiKey);

            var systemPrompt = GetSystemPrompt();

            var requestData = new
            {
                model = "gpt-3.5-turbo",
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage }
                }
            };

            var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
            var result = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"OpenAI API error: {result}");
                return "❌ OpenAI API error";
            }

            dynamic json = JsonConvert.DeserializeObject(result);
            return json?.choices[0]?.message?.content?.ToString() ?? "No response.";
        }

        public async Task<string> GetGeminiResponseAsync(string userMessage)
        {
            var systemPrompt = GetSystemPrompt();
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={_geminiApiKey}";

            using var httpClient = new HttpClient();

            var requestPayload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = systemPrompt },
                            new { text = userMessage }
                        }
                    }
                }
            };

            var content = new StringContent(JsonConvert.SerializeObject(requestPayload), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(endpoint, content);
            var result = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Gemini API error: {result}");
                return "❌ Gemini API error";
            }

            dynamic json = JsonConvert.DeserializeObject(result);
            return json?.candidates[0]?.content?.parts[0]?.text?.ToString() ?? "No response.";
        }

        public async Task<(string response, string modelUsed)> GetSmartResponseAsync(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(ScraperHostedService.LatestWebsiteContent))
            {
                _logger.LogWarning("No scraped content available. Falling back to default response.");
                return ("I don’t have the latest website content. Try asking about our services, products, location, industries, awards, or job openings.", "none");
            }

            _logger.LogInformation($"Attempting GPT response for query: {userMessage}");
            try
            {
                var gptTask = GetResponseAsync(userMessage);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
                var completed = await Task.WhenAny(gptTask, timeoutTask);

                if (completed == gptTask)
                {
                    var gptResponse = await gptTask;
                    if (!string.IsNullOrWhiteSpace(gptResponse) && !gptResponse.StartsWith("❌"))
                    {
                        _logger.LogInformation("GPT response successful.");
                        return (gptResponse, "gpt");
                    }

                    _logger.LogWarning($"GPT failed: {gptResponse}. Trying Gemini...");
                    throw new Exception($"GPT response invalid: {gptResponse}");
                }

                _logger.LogWarning("GPT timed out. Trying Gemini...");
                throw new TimeoutException("GPT timed out.");
            }
            catch (Exception gptEx)
            {
                _logger.LogInformation($"Attempting Gemini response for query: {userMessage}");
                try
                {
                    var geminiTask = GetGeminiResponseAsync(userMessage);
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
                    var completed = await Task.WhenAny(geminiTask, timeoutTask);

                    if (completed == geminiTask)
                    {
                        var geminiResponse = await geminiTask;
                        if (!string.IsNullOrWhiteSpace(geminiResponse) && !geminiResponse.StartsWith("❌"))
                        {
                            _logger.LogInformation("Gemini response successful.");
                            return (geminiResponse, "gemini");
                        }

                        _logger.LogWarning($"Gemini failed: {geminiResponse}.");
                        throw new Exception($"Gemini response invalid: {geminiResponse}");
                    }

                    _logger.LogWarning("Gemini timed out.");
                    throw new TimeoutException("Gemini timed out.");
                }
                catch (Exception geminiEx)
                {
                    _logger.LogError($"Both GPT and Gemini failed. GPT: {gptEx.Message}, Gemini: {geminiEx.Message}");
                    if (IsJobRelated(userMessage))
                    {
                        return ("❌ There is a technical issue. Please try again after some time.", "none");
                    }
                    _logger.LogInformation($"Falling back to scraped content for query: {userMessage}");
                    return (ExtractFromScrapedContent(userMessage), "scraped");
                }
            }
        }

        public bool IsJobRelated(string message)
        {
            var jobKeywords = _sectionMappings.ContainsKey("Jobs") ? _sectionMappings["Jobs"] : new List<string> { "job", "jobs", "career", "careers", "opening", "openings", "position", "vacancy", "vacancies", "apply", "application", "interview", "hiring", "recruitment", "resume", "cv" };
            return Regex.IsMatch(message, string.Join("|", jobKeywords.Select(k => $@"\b{Regex.Escape(k)}\b")), RegexOptions.IgnoreCase);
        }

        private string ExtractFromScrapedContent(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(ScraperHostedService.LatestWebsiteContent))
            {
                _logger.LogWarning("No website content available in ScraperHostedService.LatestWebsiteContent");
                return "❌ No website content available. Please try again later.";
            }

            _logger.LogDebug($"Scraped content: {ScraperHostedService.LatestWebsiteContent}");
            string content = ScraperHostedService.LatestWebsiteContent;
            string message = userMessage.ToLower();

            string GetSectionContent(string sectionKey)
            {
                var sectionNames = _sectionMappings.ContainsKey(sectionKey) ? _sectionMappings[sectionKey] : new List<string>();
                foreach (var name in sectionNames)
                {
                    var pattern = $@"🔸\s*{Regex.Escape(name.ToUpper())}:\s*(.*?)(?=(🔸|\Z))";
                    var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (match.Success)
                    {
                        return CleanContent(match.Groups[1].Value, sectionKey.ToLower());
                    }
                }
                // Fallback to AI-based extraction
                return ExtractWithAI(userMessage, sectionKey).Result;
            }

            if (Regex.IsMatch(message, @"\b(name of company|company name|what is the name of company|what's the company)\b"))
            {
                return GetFeatureContent("About", message, content);
            }
            else if (IsProductRelated(message))
            {
                return GetFeatureContent("Products", message, content);
            }
            else if (IsServiceRelated(message))
            {
                return GetFeatureContent("Services", message, content);
            }
            else if (IsLocationRelated(message) || Regex.IsMatch(message, @"\b(address|location|office|headquarter|branch)\b"))
            {
                return GetFeatureContent("Contact", message, content, isLocation: true);
            }
            else if (Regex.IsMatch(message, @"\b(industry|industries)\b"))
            {
                return GetFeatureContent("Industries", message, content);
            }
            else if (Regex.IsMatch(message, @"\b(award|awards|achievement|achievements)\b"))
            {
                return GetFeatureContent("Awards", message, content, isAwards: true);
            }
            else if (Regex.IsMatch(message, @"\b(contact|phone|email)\b"))
            {
                return GetFeatureContent("Contact", message, content);
            }
            else if (Regex.IsMatch(message, @"\b(about|who are you|mission|vision)\b"))
            {
                return GetFeatureContent("About", message, content);
            }
            else if (IsJobRelated(message))
            {
                return GetFeatureContent("Jobs", message, content);
            }

            // Fallback to AI-based extraction for unrecognized queries
            return ExtractWithAI(userMessage, "Generic").Result;
        }

        private string GetFeatureContent(string sectionKey, string message, string content, bool isLocation = false, bool isAwards = false)
        {
            var sectionNames = _sectionMappings.ContainsKey(sectionKey) ? _sectionMappings[sectionKey] : new List<string>();
            foreach (var name in sectionNames)
            {
                var pattern = $@"🔸\s*{Regex.Escape(name.ToUpper())}:\s*(.*?)(?=(🔸|\Z))";
                var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (match.Success)
                {
                    var sectionContent = CleanContent(match.Groups[1].Value, sectionKey.ToLower());
                    if (isLocation)
                    {
                        var lines = sectionContent.Split('\n')
                            .Select(line => line.Trim())
                            .Where(line => line.StartsWith("- Address:") &&
                                           !Regex.IsMatch(line, @"Font Awesome|© All Rights Reserved", RegexOptions.IgnoreCase))
                            .Select(line => Regex.Replace(line, @"^- Address:\s*", "").Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        return lines.Any() ? $"The company has offices at: {string.Join("; ", lines)}." : GetFallbackLocationContent(message, content);
                    }
                    else if (isAwards)
                    {
                        var awards = Regex.Matches(sectionContent, @"(?:ISO \d{4}-\d{4}|Middle-East Asia Leadership Summit|IWC.*Excellence in IT|Hot 100 Startup Awards|Asia Leadership Summit)[^\n]*", RegexOptions.IgnoreCase)
                            .Cast<Match>()
                            .Select(m => m.Value.Trim())
                            .ToList();
                        return awards.Any() ? $"The company has received the following awards: {string.Join("; ", awards)}." : GetFallbackAwardsContent(message, content);
                    }
                    else if (sectionKey == "Contact" && !isLocation)
                    {
                        var contactDetails = Regex.Matches(sectionContent, @"(?:📞\s*(\+?\d{1,3}[\s.-]?\d{8,12}))|(?:✉️\s*([\w-\.]+@[\w-]+\.[\w]{2,4}))", RegexOptions.IgnoreCase);
                        var contacts = contactDetails.Cast<Match>()
                            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        return contacts.Any() ? $"You can contact the company at: {string.Join(", ", contacts)}." : "I couldn’t find contact information in the available content.";
                    }
                    else if (sectionKey == "About" && Regex.IsMatch(message, @"\b(name of company|company name|what is the name of company|what's the company)\b"))
                    {
                        var companyNameMatch = Regex.Match(sectionContent, @"([^\s]+(?:\s+[^\s]+)*)\s*(Pvt\.?\s*Ltd\.?|Inc\.?|LLC|Corp\.?)", RegexOptions.IgnoreCase);
                        return companyNameMatch.Success ? $"The company name is {companyNameMatch.Groups[0].Value}." : "I couldn’t find the company name in the available content.";
                    }
                    return sectionContent;
                }
            }
            // Fallback for specific cases
            if (isLocation)
                return GetFallbackLocationContent(message, content);
            if (isAwards)
                return GetFallbackAwardsContent(message, content);
            // General AI fallback
            return ExtractWithAI(message, sectionKey).Result;
        }

        private string GetFallbackLocationContent(string message, string content)
        {
            var aboutMatch = Regex.Match(content, @"🔸\s*ABOUT:\s*(.*?)(?=(🔸|\Z))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (aboutMatch.Success)
            {
                var lines = aboutMatch.Groups[1].Value.Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => Regex.IsMatch(line, @"\b(Vadodara|Placentia|Ras al Khaimah|Gujarat|CA|Dubai|India|USA)\b", RegexOptions.IgnoreCase) &&
                                   !Regex.IsMatch(line, @"\b(mission|vision|CEO|award|software|agile|methodology|sprint|project)\b", RegexOptions.IgnoreCase))
                    .Select(line => Regex.Replace(line, @"^-|\s{2,}", "").Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return lines.Any() ? $"The company has offices at: {string.Join("; ", lines)}." : "I couldn’t find location information in the available content.";
            }
            return ExtractWithAI(message, "Contact").Result;
        }

        private string GetFallbackAwardsContent(string message, string content)
        {
            var aboutMatch = Regex.Match(content, @"🔸\s*ABOUT:\s*(.*?)(?=(🔸|\Z))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (aboutMatch.Success)
            {
                var awards = Regex.Matches(aboutMatch.Groups[1].Value, @"(?:ISO \d{4}-\d{4}|Middle-East Asia Leadership Summit|IWC.*Excellence in IT|Hot 100 Startup Awards|Asia Leadership Summit)[^\n]*", RegexOptions.IgnoreCase)
                    .Cast<Match>()
                    .Select(m => m.Value.Trim())
                    .ToList();
                return awards.Any() ? $"The company has received the following awards: {string.Join("; ", awards)}." : "I couldn’t find award information in the available content.";
            }
            return ExtractWithAI(message, "Awards").Result;
        }

        private async Task<string> ExtractWithAI(string userMessage, string sectionKey)
        {
            var cacheKey = $"{sectionKey}:{userMessage}";
            if (_responseCache.TryGetValue(cacheKey, out var cachedResponse))
            {
                _logger.LogInformation($"Returning cached AI response for {cacheKey}");
                return cachedResponse;
            }

            var prompt = $@"Given the following website content and user query, extract relevant information for the requested category ({sectionKey}). If no relevant information is found, return: 'I couldn’t find information about {sectionKey.ToLower()} in the available content.'

            === Website Content ===
            {ScraperHostedService.LatestWebsiteContent}
            === End Website Content ===

            === User Query ===
            {userMessage}
            === End User Query ===

            Return only the extracted information or the error message.";

            var (response, _) = await GetSmartResponseAsync(prompt);
            _responseCache[cacheKey] = response;
            return response;
        }

        private string CleanContent(string rawContent, string contentType)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                _logger.LogWarning($"No {contentType} content found in scraped data.");
                return $"No {contentType} content found.";
            }

            rawContent = Regex.Replace(rawContent, @"<[^>]+>|Font Awesome fontawesome\.com\s*-->|© All Rights Reserved", "");
            rawContent = Regex.Replace(rawContent, @"\s+", " ").Trim();
            rawContent = Regex.Replace(rawContent, @"[-–—|]+", ", ");
            var items = rawContent.Split(new[] { ", ", "; ", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item) &&
                               item.Length > 10 &&
                               !Regex.IsMatch(item, @"Sign up|Important Links|Social Links", RegexOptions.IgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string result;
            if (contentType == "industries" || contentType == "services" || contentType == "products" || contentType == "jobs")
            {
                result = items.Any() ? string.Join(", ", items) : rawContent;
            }
            else if (contentType == "about")
            {
                items = items.Where(item => !Regex.IsMatch(item, @"(?:\d+\s*[^\n]*?,.*?\d{5,})|(?:ISO \d{4}-\d{4})|(?:\+\d{1,3}\s*\d{8,12})|(?:[\w-\.]+@[\w-]+\.[\w]{2,4})", RegexOptions.IgnoreCase)).ToList();
                result = items.Any() ? string.Join(" ", items) : rawContent;
            }
            else if (contentType == "location" || contentType == "awards")
            {
                result = string.Join("; ", items);
            }
            else
            {
                result = items.Any() ? string.Join(" ", items) : rawContent;
            }

            result = result.Length > 500 ? result.Substring(0, 497) + "..." : result;
            _logger.LogDebug($"Cleaned {contentType} content: {result}");
            return result;
        }

        public async Task<(string response, string modelUsed)> AskWithFallbackAsync(string prompt, string? fallbackPrompt = null)
        {
            return await GetSmartResponseAsync(fallbackPrompt ?? prompt);
        }

        public async Task<(string response, string modelUsed)> GenerateInterviewQuestionsAsync(string jobTitle)
        {
            string normalizedJobTitle = jobTitle.Trim();

            if (_jobOpeningsStatus.ContainsKey(normalizedJobTitle) && _jobOpeningsStatus[normalizedJobTitle] == 0)
            {
                _logger.LogInformation($"Job '{jobTitle}' is disabled in configuration.");
                return ("❌ The job is currently not available.", "none");
            }

            if (_interviewQuestionSource == "manual" && _manualInterviewQuestions.ContainsKey(normalizedJobTitle))
            {
                var questions = _manualInterviewQuestions[normalizedJobTitle];
                if (questions == null || !questions.Any())
                {
                    _logger.LogWarning($"No manual questions found for job '{jobTitle}'. Falling back to default questions.");
                    questions = _fallbackQuestions;
                }
                var selectedQuestions = questions.OrderBy(x => Guid.NewGuid()).Take(_interviewQuestionCount).ToList();
                var response = string.Join("\n", selectedQuestions.Select((q, i) => $"{i + 1}. {q}"));
                return (response, "manual");
            }
            else
            {
                if (_interviewQuestionSource == "manual")
                {
                    _logger.LogWarning($"Manual questions requested but none found for job '{jobTitle}'. Using fallback questions.");
                    var questions = _fallbackQuestions;
                    var selectedQuestions = questions.OrderBy(x => Guid.NewGuid()).Take(_interviewQuestionCount).ToList();
                    var response = string.Join("\n", selectedQuestions.Select((q, i) => $"{i + 1}. {q}"));
                    return (response, "manual");
                }

                string prompt = $@"
Generate {_interviewQuestionCount} technical interview questions for the role '{jobTitle}' using only the website content provided in the '🔸 DETAILED JOB INFO:' section.
Return only the questions, no explanations or answers.";
                return await GetSmartResponseAsync(prompt);
            }
        }

        //        public async Task<(List<string> Questions, string Model)> GenerateRandomInterviewQuestionsWithModelAsync(string jobTitle, int count = 5)
        //        {
        //            string normalizedJobTitle = jobTitle.Trim();

        //            if (_jobOpeningsStatus.ContainsKey(normalizedJobTitle) && _jobOpeningsStatus[normalizedJobTitle] == 0)
        //            {
        //                _logger.LogInformation($"Job '{jobTitle}' is disabled in configuration.");
        //                return (new List<string>(), "none");
        //            }

        //            if (_interviewQuestionSource == "manual" && _manualInterviewQuestions.ContainsKey(normalizedJobTitle))
        //            {
        //                var questions = _manualInterviewQuestions[normalizedJobTitle];
        //                if (questions == null || !questions.Any())
        //                {
        //                    _logger.LogWarning($"No manual questions found for job '{jobTitle}'. Falling back to default questions.");
        //                    questions = _fallbackQuestions;
        //                }
        //                var selectedQuestions = questions.OrderBy(x => Guid.NewGuid()).Take(Math.Min(_interviewQuestionCount, count)).ToList();
        //                return (selectedQuestions, "manual");
        //            }
        //            else
        //            {
        //                if (_interviewQuestionSource == "manual")
        //                {
        //                    _logger.LogWarning($"Manual questions requested but none found for job '{jobTitle}'. Using fallback questions.");
        //                    var questions = _fallbackQuestions;
        //                    var selectedQuestions = questions.OrderBy(x => Guid.NewGuid()).Take(Math.Min(_interviewQuestionCount, count)).ToList();
        //                    return (selectedQuestions, "manual");
        //                }

        //                string prompt = $@"
        //Pick {_interviewQuestionCount} random technical interview questions for the role '{jobTitle}' using only the website content provided in the '🔸 DETAILED JOB INFO:' section.
        //Return only the questions in a plain numbered list without explanation.";
        //                var (response, model) = await GetSmartResponseAsync(prompt);
        //                var questions = response.Split('\n')
        //                    .Where(l => !string.IsNullOrWhiteSpace(l))
        //                    .Select(l => Regex.Replace(l.Trim(), @"^\d+[\.\)]\s*", ""))
        //                    .Take(_interviewQuestionCount)
        //                    .ToList();
        //                return (questions, model);
        //            }
        //        }

        //        public async Task<(List<string> Questions, string Model)> GenerateRandomInterviewQuestionsWithModelAsync(string jobTitle, int count = 5)
        //        {
        //            string normalizedJobTitle = jobTitle.Trim();

        //            if (_jobOpeningsStatus.ContainsKey(normalizedJobTitle) && _jobOpeningsStatus[normalizedJobTitle] == 0)
        //            {
        //                _logger.LogInformation($"Job '{jobTitle}' is disabled in configuration.");
        //                return (new List<string>(), "none");
        //            }

        //            if (_interviewQuestionSource == "manual" && _manualInterviewQuestions.ContainsKey(normalizedJobTitle))
        //            {
        //                var questions = _manualInterviewQuestions[normalizedJobTitle];
        //                if (questions == null || !questions.Any())
        //                {
        //                    _logger.LogWarning($"No manual questions found for job '{jobTitle}'. Falling back to default questions.");
        //                    questions = _fallbackQuestions;
        //                }
        //                var selectedQuestions = questions.OrderBy(x => Guid.NewGuid()).Take(Math.Min(_interviewQuestionCount, count)).ToList();
        //                return (selectedQuestions, "manual");
        //            }
        //            else
        //            {
        //                if (_interviewQuestionSource == "manual")
        //                {
        //                    _logger.LogWarning($"Manual questions requested but none found for job '{jobTitle}'. Using fallback questions.");
        //                    var questions = _fallbackQuestions;
        //                    var selectedQuestions = questions.OrderBy(x => Guid.NewGuid()).Take(Math.Min(_interviewQuestionCount, count)).ToList();
        //                    return (selectedQuestions, "manual");
        //                }

        //                string prompt = $@"
        //Pick {_interviewQuestionCount} random technical interview questions for the role '{jobTitle}' using only the website content provided in the '🔸 DETAILED JOB INFO:' section.
        //Return only the questions in a plain numbered list without explanation.";
        //                var (response, model) = await GetSmartResponseAsync(prompt);
        //                var aiQuestions = response.Split('\n')
        //                    .Where(l => !string.IsNullOrWhiteSpace(l))
        //                    .Select(l => Regex.Replace(l.Trim(), @"^\d+[\.\)]\s*", ""))
        //                    .Take(_interviewQuestionCount)
        //                    .ToList();
        //                return (aiQuestions, model);
        //            }
        //        }

        public async Task<(List<string> Questions, string Model)> GenerateRandomInterviewQuestionsWithModelAsync(string jobTitle)
        {
            string normalizedJobTitle = jobTitle.Trim();

            if (_jobOpeningsStatus.ContainsKey(normalizedJobTitle) && _jobOpeningsStatus[normalizedJobTitle] == 0)
            {
                _logger.LogInformation($"Job '{jobTitle}' is disabled in configuration.");
                return (new List<string>(), "none");
            }

            if (_interviewQuestionSource == "manual" && _manualInterviewQuestions.ContainsKey(normalizedJobTitle))
            {
                var questions = _manualInterviewQuestions[normalizedJobTitle];
                if (questions == null || !questions.Any())
                {
                    _logger.LogWarning($"No manual questions found for job '{jobTitle}'. Falling back to default questions.");
                    questions = _fallbackQuestions;
                }
                var selectedQuestions = questions.OrderBy(x => Guid.NewGuid()).Take(_interviewQuestionCount).ToList();
                return (selectedQuestions, "manual");
            }
            else
            {
                if (_interviewQuestionSource == "manual")
                {
                    _logger.LogWarning($"Manual questions requested but none found for job '{jobTitle}'. Using fallback questions.");
                    var questions = _fallbackQuestions;
                    var selectedQuestions = questions.OrderBy(x => Guid.NewGuid()).Take(_interviewQuestionCount).ToList();
                    return (selectedQuestions, "manual");
                }

                string prompt = $@"
Pick {_interviewQuestionCount} random technical interview questions for the role '{jobTitle}' using only the website content provided in the '🔸 DETAILED JOB INFO:' section.
Return only the questions in a plain numbered list without explanation.";
                var (response, model) = await GetSmartResponseAsync(prompt);
                var aiQuestions = response.Split('\n')
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => Regex.Replace(l.Trim(), @"^\d+[\.\)]\s*", ""))
                    .Take(_interviewQuestionCount)
                    .ToList();
                return (aiQuestions, model);
            }
        }

        public async Task<(List<string> Jobs, string Model)> FindJobsByResumeAsync(string resumeText)
        {
            var enabledJobs = _jobOpeningsStatus
                .Where(kvp => kvp.Value == 1)
                .Select(kvp => kvp.Key)
                .Select(k => char.ToUpper(k[0]) + k.Substring(1))
                .ToList();

            if (!enabledJobs.Any())
            {
                _logger.LogWarning("No enabled job openings found in JobOpeningsStatus.");
                return (new List<string>(), "none");
            }

            // Truncate resume text to avoid exceeding token limits
            resumeText = resumeText.Length > 2000 ? resumeText.Substring(0, 2000) + "..." : resumeText;

            string prompt = $@"
Given the following resume text and list of job openings, identify which job titles best match the candidate's skills and experience. Return only the matching job titles in a numbered list. If no jobs match, return an empty list. Do not include jobs not listed below.

=== Resume Text ===
{resumeText}
=== End Resume Text ===

=== Job Openings ===
{string.Join("\n", enabledJobs.Select((job, index) => $"{index + 1}. {job}"))}
=== End Job Openings ===

Return only the job titles that are a good match, in a numbered list (e.g., 1. Job Title). If no matches, return an empty list.";
            var (response, model) = await GetSmartResponseAsync(prompt);

            var matchingJobs = new List<string>();
            if (!string.IsNullOrWhiteSpace(response) && !response.StartsWith("❌"))
            {
                matchingJobs = response.Split('\n')
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => Regex.Replace(line.Trim(), @"^\d+[\.\)]\s*", ""))
                    .Where(job => enabledJobs.Contains(job, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            _logger.LogInformation($"Resume matched jobs: {string.Join(", ", matchingJobs)}");
            return (matchingJobs, model);
        }

        public async Task<bool> IsJobIntentAsync(string userInput)
        {
            // Normalize input to handle case sensitivity and extra whitespace
            string normalizedInput = Regex.Replace(userInput.Trim(), @"\s+", " ").ToLower();
            _logger.LogInformation($"Normalized input for job intent check: {normalizedInput}");

            // Load job keywords from configuration or use defaults
            var jobKeywords = _sectionMappings.ContainsKey("Jobs") && _sectionMappings["Jobs"] != null && _sectionMappings["Jobs"].Count > 0
                ? _sectionMappings["Jobs"]
                : new List<string> { "job", "jobs", "career", "careers", "opening", "openings", "position", "vacancy", "vacancies", "apply", "application", "interview", "hiring", "recruitment", "resume", "cv" };

            _logger.LogInformation($"Job keywords used: {string.Join(", ", jobKeywords)}");

            // Create regex pattern for keyword matching
            var pattern = string.Join("|", jobKeywords.Select(k => $@"\b{Regex.Escape(k.ToLower())}\b"));
            _logger.LogInformation($"Regex pattern: {pattern}");

            // Check if input contains job-related keywords
            if (Regex.IsMatch(normalizedInput, pattern, RegexOptions.IgnoreCase))
            {
                _logger.LogInformation($"Regex matched keywords in: {normalizedInput}");

                // Explicitly check for application-related phrases
                var applicationPattern = @"\b(apply|application|resume|cv)\b.*\b(job|jobs|career|careers|opening|openings|position|vacancy|vacancies)\b|\b(job|jobs|career|careers|opening|openings|position|vacancy|vacancies)\b.*\b(apply|application|resume|cv)\b";
                if (Regex.IsMatch(normalizedInput, applicationPattern, RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation($"Job application intent confirmed via regex for: {normalizedInput}");
                    return true;
                }

                // AI-based confirmation for ambiguous cases
                var prompt = $@"Does this message explicitly ask about job openings, job applications, or career opportunities? Ignore general company inquiries like services or location. Answer only 'yes' or 'no'.
Examples:
- 'What jobs are available?' → yes
- 'I want to apply for a job' → yes
- 'I want to apply for a job in this company' → yes
- 'Tell me about your services' → no
- 'Where is your office?' → no
- 'What is the company about?' → no
User: {userInput}";
                var (reply, modelUsed) = await GetSmartResponseAsync(prompt);
                bool isJobIntent = reply.Trim().ToLower().StartsWith("yes");
                _logger.LogInformation($"IsJobIntentAsync for '{userInput}' returned '{reply}' (model: {modelUsed}, isJobIntent: {isJobIntent})");
                return isJobIntent;
            }

            _logger.LogInformation($"No job-related keywords matched in: {normalizedInput}");
            return false;
        }

        public async Task<bool> IsLocationIntentAsync(string userInput)
        {
            var locationKeywords = _sectionMappings.ContainsKey("Contact") ? _sectionMappings["Contact"] : new List<string> { "location", "where", "address", "office", "located", "headquarter", "branch", "place" };
            if (Regex.IsMatch(userInput, string.Join("|", locationKeywords.Select(k => $@"\b{Regex.Escape(k)}\b")), RegexOptions.IgnoreCase))
                return true;

            var prompt = $"Is this message asking for the company's location or address? Answer only 'yes' or 'no'.\nUser: {userInput}";
            var (reply, _) = await GetSmartResponseAsync(prompt);
            return reply.Trim().ToLower().StartsWith("yes");
        }

        public async Task<(List<string> Jobs, string Model)> GetJobOpeningsAsync()
        {
            var enabledJobs = _jobOpeningsStatus
                .Where(kvp => kvp.Value == 1)
                .Select(kvp => kvp.Key)
                .Select(k => char.ToUpper(k[0]) + k.Substring(1))
                .ToList();

            if (enabledJobs.Any())
            {
                _logger.LogInformation($"Returning enabled job openings from config: {string.Join(", ", enabledJobs)}");
                return (enabledJobs, "config");
            }

            string prompt = "List all current job openings from the '🔸 JOBS:' or '🔸 DETAILED JOB INFO:' section of the website content. Return only the job titles in a bullet or numbered list. If no job openings are found, return: 'No job openings found at the moment.'";
            var (response, model) = await GetSmartResponseAsync(prompt);

            if (response.Contains("No job openings found") || string.IsNullOrWhiteSpace(response) || response.StartsWith("❌"))
            {
                _logger.LogWarning($"GetJobOpeningsAsync returned: {response}. No valid job openings found.");
                return (new List<string>(), model);
            }

            var jobs = response.Split('\n')
                .Select(line => Regex.Replace(line.Trim(), @"^\d+[\.\)]\s*|- ", ""))
                .Where(j => !string.IsNullOrWhiteSpace(j))
                .ToList();

            if (jobs.Count == 0)
            {
                _logger.LogWarning("No enabled job openings found after filtering.");
                return (new List<string>(), model);
            }

            _logger.LogInformation($"Enabled job openings retrieved: {string.Join(", ", jobs)}");
            return (jobs, model);
        }

        public bool IsCompanyRelated(string message)
        {
            var jobKeywords = _sectionMappings.ContainsKey("Jobs") ? _sectionMappings["Jobs"] : new List<string> { "job", "jobs", "career", "careers", "opening", "openings", "position", "vacancy", "vacancies", "apply", "application", "interview", "hiring", "recruitment", "resume", "cv" };
            var companyKeywords = _sectionMappings.SelectMany(kvp => kvp.Value).Distinct().ToList();
            companyKeywords.AddRange(new[] { "company", "mission", "vision", "award", "awards" });
            companyKeywords = companyKeywords.Except(jobKeywords).ToList(); // Exclude job-related keywords

            message = message.ToLower();
            return companyKeywords.Any(k => message.Contains(k.ToLower())) &&
                   !Regex.IsMatch(message, @"\b(apply|application|resume|cv)\b.*\b(job|jobs|career|careers|opening|openings|position|vacancy|vacancies)\b", RegexOptions.IgnoreCase) &&
                   !Regex.IsMatch(message, @"\b(job|jobs|career|careers|opening|openings|position|vacancy|vacancies)\b.*\b(apply|application|resume|cv)\b", RegexOptions.IgnoreCase);
        }

        public bool IsServiceRelated(string message)
        {
            var serviceKeywords = _sectionMappings.ContainsKey("Services") ? _sectionMappings["Services"] : new List<string> { "service", "services", "offer", "provide", "solution", "solutions", "what we offer" };
            message = message.ToLower();
            return serviceKeywords.Any(k => message.Contains(k.ToLower())) && !message.Contains("product");
        }

        public bool IsProductRelated(string message)
        {
            var productKeywords = _sectionMappings.ContainsKey("Products") ? _sectionMappings["Products"] : new List<string> { "product", "products", "solutions" };
            message = message.ToLower();
            return productKeywords.Any(k => message.Contains(k.ToLower()));
        }

        public bool IsLocationRelated(string message)
        {
            var locationKeywords = _sectionMappings.ContainsKey("Contact") ? _sectionMappings["Contact"] : new List<string> { "location", "where", "address", "office", "located", "headquarter", "branch", "place" };
            message = message.ToLower();
            return locationKeywords.Any(k => message.Contains(k.ToLower())) ||
                   Regex.IsMatch(message, @"\b(where|located|address|office|headquarter|branch)\b.*\b(company|business)\b", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(message, @"\b(company|business)\b.*\b(where|located|address|office|headquarter|branch)\b", RegexOptions.IgnoreCase);
        }
    }
}