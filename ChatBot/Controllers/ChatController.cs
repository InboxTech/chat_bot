using ChatBot.Models;
using ChatBot.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace ChatBot.Controllers
{
    public class ChatController : Controller
    {
        private readonly ChatGPTService _chatGPTService;
        private readonly ChatDbService _chatDbService;

        public ChatController(ChatGPTService chatGPTService, ChatDbService chatDbService)
        {
            _chatGPTService = chatGPTService;
            _chatDbService = chatDbService;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage([FromBody] ChatMessage userMsg)
        {
            string msg = userMsg.UserMessage?.Trim() ?? "";
            string userId = HttpContext.Session.Id;
            string response = "";
            string modelUsed = "custom";

            var message = new ChatMessage
            {
                UserMessage = msg,
                CreatedAt = DateTime.Now
            };

            try
            {
                // Step 1: Greeting
                if (Regex.IsMatch(msg, @"\b(hi|hello|hey)\b", RegexOptions.IgnoreCase))
                {
                    response = "👋 Hello! I’m the official chatbot of Inbox Infotech Pvt. Ltd. How can I assist you today?";
                }

                // Step 2: Continue ongoing interview
                else if (_chatDbService.GetLatestSession(userId) is InterviewSession session && session.QuestionIndex < session.Questions.Count)
                {
                    session.Answers.Add(msg);
                    session.QuestionIndex++;

                    if (session.QuestionIndex < session.Questions.Count)
                        response = $"❓ Question {session.QuestionIndex + 1}: {session.Questions[session.QuestionIndex]}";
                    else
                        response = $"✅ Thank you for completing the interview for the position of {session.JobTitle}.\nOur team will review your responses.";

                    _chatDbService.UpdateInterviewSession(session);
                }

                // ✅ Step 3: Handle job selection BEFORE job intent check
                else if (HttpContext.Session.GetString("JobList") is string jobListStr && !string.IsNullOrWhiteSpace(jobListStr))
                {
                    var jobList = jobListStr.Split("||").ToList();
                    string selectedJob = "";

                    if (int.TryParse(msg, out int selectedIndex) && selectedIndex >= 1 && selectedIndex <= jobList.Count)
                    {
                        selectedJob = jobList[selectedIndex - 1];
                    }
                    else
                    {
                        selectedJob = jobList.FirstOrDefault(j => msg.ToLower().Contains(j.ToLower())) ?? "";
                    }

                    

                    if (!string.IsNullOrEmpty(selectedJob))
                    {
                        var (questions, model) = await _chatGPTService.GenerateRandomInterviewQuestionsWithModelAsync(selectedJob, 5);

                        var newSession = new InterviewSession
                        {
                            UserId = userId,
                            JobTitle = selectedJob,
                            Questions = questions,
                            QuestionIndex = 0
                        };

                        _chatDbService.SaveInterviewSession(newSession);
                        response = $"🧪 Starting interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
                        modelUsed = model;

                        // 🔁 Clear the JobList so it's not used again accidentally
                        HttpContext.Session.Remove("JobList");
                    }

                    else
                    {
                        response = "❌ I couldn’t match your response to any job. Please type the job title or number.";
                    }
                }

                // Step 4: Detect job intent
                else if (await _chatGPTService.IsJobIntentAsync(msg))
                {
                    var (jobList, model) = await _chatGPTService.GetJobOpeningsAsync();
                    modelUsed = model;

                    if (jobList.Count > 0)
                    {
                        response = "🧑‍💻 Current job openings at Inbox Infotech:\n";
                        for (int i = 0; i < jobList.Count; i++)
                            response += $"{i + 1}. {jobList[i]}\n";

                        response += "\nPlease reply with the job title or number you'd like to apply for.";

                        HttpContext.Session.SetString("JobList", string.Join("||", jobList));
                    }
                    else
                    {
                        response = "❌ Sorry, no job openings found at the moment.";
                    }
                }


                // Step 5: Location
                else if (await _chatGPTService.IsLocationIntentAsync(msg))
                {
                    var (resp, model) = await _chatGPTService.AskWithFallbackAsync("What is the location of the company?", msg);
                    response = resp;
                    modelUsed = model;
                }

                // Step 6: Services
                else if (msg.Contains("service"))
                {
                    var (resp, model) = await _chatGPTService.AskWithFallbackAsync("What services does the company provide?", msg);
                    response = resp;
                    modelUsed = model;
                }

                // Step 7: About
                else if (msg.Contains("about") || msg.Contains("company"))
                {
                    var (resp, model) = await _chatGPTService.AskWithFallbackAsync("Tell me about the company.", msg);
                    response = resp;
                    modelUsed = model;
                }

                // Step 8: Other company-related
                else if (_chatGPTService.IsCompanyRelated(msg))
                {
                    var (resp, model) = await _chatGPTService.GetSmartResponseAsync(msg);
                    response = resp;
                    modelUsed = model;
                }

                else
                {
                    response = "❌ I can only help with information related to Inbox Infotech Pvt. Ltd.";
                }
            }
            catch (Exception ex)
            {
                response = $"❌ Unexpected error: {ex.Message}";
                modelUsed = "error";
            }

            message.BotResponse = response;
            message.Model = modelUsed;
            _chatDbService.SaveMessage(message);

            return Json(new { response, model = modelUsed });
        }

        [HttpPost]
        public IActionResult UpdateTabSwitchCount([FromBody] TabSwitchModel data)
        {
            string userId = HttpContext.Session.Id;
            _chatDbService.UpdateTabSwitchCount(userId, data.Count);
            return Ok();
        }

        public class TabSwitchModel
        {
            public int Count { get; set; }
        }

    }
}
