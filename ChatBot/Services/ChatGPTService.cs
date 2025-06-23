using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace ChatBot.Services
{

    public class ChatGPTService
    {
        private readonly string _openAIApiKey;
        private readonly string _geminiApiKey;

        public ChatGPTService(IConfiguration config)
        {
            _openAIApiKey = config["OpenAI:ApiKey"];
            _geminiApiKey = config["Gemini:ApiKey"];
        }

        private string GetSystemPrompt()
        {
            return $@"
You are a helpful and accurate chatbot for Inbox Infotech Pvt. Ltd.
Use ONLY the content between === markers to answer user queries or generate interview questions.
If the requested information is not present in the content, politely say you cannot help.

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
                return "❌ OpenAI API error";

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
                return "❌ Gemini API error";

            dynamic json = JsonConvert.DeserializeObject(result);
            return json?.candidates[0]?.content?.parts[0]?.text?.ToString() ?? "No response.";
        }

        public async Task<(string response, string modelUsed)> GetSmartResponseAsync(string userMessage)
        {
            try
            {
                var gptTask = GetResponseAsync(userMessage);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
                var completed = await Task.WhenAny(gptTask, timeoutTask);

                if (completed == gptTask)
                {
                    var gptResponse = await gptTask;
                    if (!string.IsNullOrWhiteSpace(gptResponse) && !gptResponse.StartsWith("❌"))
                        return (gptResponse, "gpt");

                    throw new Exception("GPT returned empty or error.");
                }

                throw new TimeoutException("GPT timed out.");
            }
            catch (Exception gptEx)
            {
                try
                {
                    var geminiResponse = await GetGeminiResponseAsync(userMessage);
                    if (!string.IsNullOrWhiteSpace(geminiResponse) && !geminiResponse.StartsWith("❌"))
                        return (geminiResponse, "gemini");

                    return ($"{geminiResponse}\n(GPT failed: {gptEx.Message})", "gemini");
                }
                catch (Exception geminiEx)
                {
                    return ($"❌ Both GPT and Gemini failed.\nGPT: {gptEx.Message}\nGemini: {geminiEx.Message}", "none");
                }
            }
        }

        public async Task<(string response, string modelUsed)> AskWithFallbackAsync(string prompt, string? fallbackPrompt = null)
        {
            return await GetSmartResponseAsync(fallbackPrompt ?? prompt);
        }

        public async Task<(string response, string modelUsed)> GenerateInterviewQuestionsAsync(string jobTitle)
        {
            string prompt = $@"
Generate 4 to 5 technical interview questions for the role '{jobTitle}' using only the website content provided. 
Return only the questions, no explanations or answers.";

            return await GetSmartResponseAsync(prompt);
        }

        //        public async Task<List<string>> GenerateRandomInterviewQuestionsAsync(string jobTitle, int count = 5)
        //        {
        //            string prompt = $@"
        //Pick {count} random technical interview questions for the role '{jobTitle}' using only the website content provided. 
        //Return only the questions in a plain numbered list without explanation.";

        //            var (response, _) = await GetSmartResponseAsync(prompt);

        //            return response.Split('\n')
        //                .Where(l => !string.IsNullOrWhiteSpace(l))
        //                .Select(l => Regex.Replace(l.Trim(), @"^\d+[\.\)]\s*", "")) // Remove numbering
        //                .Take(count)
        //                .ToList();
        //        }

        public async Task<(List<string> Questions, string Model)> GenerateRandomInterviewQuestionsWithModelAsync(string jobTitle, int count = 5)
        {
            string prompt = $@"
Pick {count} random technical interview questions for the role '{jobTitle}' using only the website content provided. 
Return only the questions in a plain numbered list without explanation.";

            var (response, model) = await GetSmartResponseAsync(prompt);

            var questions = response.Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => Regex.Replace(l.Trim(), @"^\d+[\.\)]\s*", "")) // Remove numbering
                .Take(count)
                .ToList();

            return (questions, model);
        }


        public async Task<bool> IsJobIntentAsync(string userInput)
        {
            var prompt = $"Is this message asking about job openings or applying for a job at Inbox Infotech? Answer only 'yes' or 'no'.\nUser: {userInput}";
            var (reply, _) = await GetSmartResponseAsync(prompt);
            return reply.ToLower().Contains("yes");
        }

        public async Task<bool> IsLocationIntentAsync(string userInput)
        {
            var prompt = $"Is this message asking for the company's location or address? Answer only 'yes' or 'no'.\nUser: {userInput}";
            var (reply, _) = await GetSmartResponseAsync(prompt);
            return reply.ToLower().Contains("yes");
        }

        public async Task<(List<string> Jobs, string Model)> GetJobOpeningsAsync()
        {
            string prompt = "List all current job openings at Inbox Infotech from the website. Return only the job titles in a bullet or numbered list.";
            var (response, model) = await GetSmartResponseAsync(prompt);

            var jobs = response.Split('\n')
                .Select(line => Regex.Replace(line.Trim(), @"^\d+[\.\)]\s*|- ", ""))
                .Where(j => !string.IsNullOrWhiteSpace(j))
                .ToList();

            return (jobs, model);
        }


        public bool IsCompanyRelated(string message)
        {
            string[] keywords = {
                "inbox", "infotech", "career", "job", "location", "apply",
                "interview", "react", "node", "ui", "ux", "services", "about", "contact"
            };

            message = message.ToLower();
            return keywords.Any(k => message.Contains(k));
        }
    }
}
