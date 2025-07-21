using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ChatBot.Models;
using ChatBot.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Xceed.Words.NET;
using System.Text;

namespace ChatBot.Controllers
{
    public class ChatController : Controller
    {
        private readonly ChatGPTService _chatGPTService;
        private readonly ChatDbService _chatDbService;
        private readonly ILogger<ChatController> _logger;
        private readonly List<PreInterviewQuestion> _preInterviewQuestions;

        public ChatController(
            ChatGPTService chatGPTService,
            ChatDbService chatDbService,
            ILogger<ChatController> logger,
            IConfiguration configuration)
        {
            _chatGPTService = chatGPTService;
            _chatDbService = chatDbService;
            _logger = logger;
            _preInterviewQuestions = configuration.GetSection("PreInterviewQuestions")
                                                  .Get<List<PreInterviewQuestion>>();
        }

        public class PreInterviewQuestion
        {
            public string State { get; set; }
            public string Prompt { get; set; }
            public string ValidationRegex { get; set; }
            public string ErrorMessage { get; set; }
            public bool SkipAllowed { get; set; }
            public string SkipToState { get; set; }
            public Dictionary<string, string> ConditionalNextStates { get; set; }
        }

        [HttpGet]
        public IActionResult Index()
        {
            HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(new List<ChatMessage>()));
            HttpContext.Session.SetString("UserIdentity", "");
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

            var sessionMessages = HttpContext.Session.GetString("SessionMessages") is string messagesStr
                ? JsonConvert.DeserializeObject<List<ChatMessage>>(messagesStr) ?? new List<ChatMessage>()
                : new List<ChatMessage>();
            var userIdentity = HttpContext.Session.GetString("UserIdentity");

            // Declare userDetails and userDetailsStr once at method scope
            UserDetails userDetails = null;
            string userDetailsStr = HttpContext.Session.GetString("UserDetails");

            _logger.LogInformation($"User query: {msg}");
            _logger.LogInformation($"IsJobIntentAsync: {await _chatGPTService.IsJobIntentAsync(msg)}");
            _logger.LogInformation($"IsLocationIntentAsync: {await _chatGPTService.IsLocationIntentAsync(msg)}");
            _logger.LogInformation($"IsCompanyRelated: {_chatGPTService.IsCompanyRelated(msg)}");
            _logger.LogInformation($"IsServiceRelated: {_chatGPTService.IsServiceRelated(msg)}");
            _logger.LogInformation($"IsProductRelated: {_chatGPTService.IsProductRelated(msg)}");
            _logger.LogInformation($"IsLocationRelated: {_chatGPTService.IsLocationRelated(msg)}");

            try
            {
                // Helper method to extract name from input
                string ExtractName(string input)
                {
                    input = input.Trim();
                    var patterns = new[]
                    {
                        @"^(?:my name is|i am|name is|call me)\s+([a-zA-Z\s]+)$",
                        @"^([a-zA-Z\s]+)$"
                    };

                    foreach (var pattern in patterns)
                    {
                        var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
                        if (match.Success && match.Groups.Count > 1)
                        {
                            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(match.Groups[1].Value.Trim().ToLower());
                        }
                    }

                    return input;
                }

                // Helper method to clear application-related session data
                void ClearApplicationState()
                {
                    HttpContext.Session.Remove("ApplicationState");
                    HttpContext.Session.Remove("UserDetails");
                    HttpContext.Session.Remove("SelectedJob");
                    HttpContext.Session.Remove("JobList");
                    HttpContext.Session.Remove("ResumeContent");
                }

                // Helper method to save and clear session messages
                void SaveAndClearSessionMessages(string name = null, string phone = null, string email = null, bool finalizeIdentity = false)
                {
                    if (sessionMessages.Count > 0)
                    {
                        if (finalizeIdentity && string.IsNullOrEmpty(userIdentity))
                        {
                            if (!string.IsNullOrEmpty(name))
                            {
                                if (!string.IsNullOrEmpty(phone))
                                    userIdentity = $"{name.Replace(" ", "_")}_{phone.Replace(" ", "").Replace("-", "")}";
                                else if (!string.IsNullOrEmpty(email))
                                    userIdentity = $"{name.Replace(" ", "_")}_{email.Replace(" ", "").Replace("@", "_").Replace(".", "_")}";
                                else
                                    userIdentity = $"{name.Replace(" ", "_")}_{DateTime.Now:yyyyMMdd_HHmmss}";
                            }
                            else if (!string.IsNullOrEmpty(phone))
                            {
                                userIdentity = $"{userId}_{phone.Replace(" ", "").Replace("-", "")}";
                            }
                            else if (!string.IsNullOrEmpty(email))
                            {
                                userIdentity = $"{userId}_{email.Replace(" ", "").Replace("@", "_").Replace(".", "_")}";
                            }

                            if (!string.IsNullOrEmpty(userIdentity))
                            {
                                HttpContext.Session.SetString("UserIdentity", userIdentity);
                                var folderPath = @"C:\Conversation";
                                var oldFileName = HttpContext.Session.GetString("SessionFileName") ?? $"session_{userId}.txt";
                                var oldPath = Path.Combine(folderPath, oldFileName);

                                string finalFileName = "";
                                if (!string.IsNullOrEmpty(name))
                                {
                                    var safeName = name.Replace(" ", "_").Replace(":", "").Replace("\\", "").Replace("/", "");
                                    if (!string.IsNullOrEmpty(phone))
                                        finalFileName = $"{safeName}_{phone.Replace(" ", "").Replace("-", "")}.txt";
                                    else if (!string.IsNullOrEmpty(email))
                                        finalFileName = $"{safeName}_{email.Replace(" ", "").Replace("@", "_").Replace(".", "_")}.txt";
                                    else
                                        finalFileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                                }
                                else
                                {
                                    finalFileName = $"{userId}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                                }

                                var newPath = Path.Combine(folderPath, finalFileName);
                                if (System.IO.File.Exists(oldPath) && oldPath != newPath)
                                {
                                    System.IO.File.Move(oldPath, newPath, overwrite: true);
                                }

                                HttpContext.Session.SetString("SessionFileName", finalFileName);
                            }
                        }

                        _chatDbService.SaveFullConversation(userId, name, phone, email, sessionMessages);
                        sessionMessages.Clear();
                        HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(sessionMessages));
                    }
                }

                async Task<string> HandleCompanyQuery(string query, string followUpPrompt = "")
                {
                    _logger.LogInformation($"Processing company query: {query}");
                    var (resp, model) = await _chatGPTService.GetSmartResponseAsync(query);
                    modelUsed = model;
                    return resp + (string.IsNullOrEmpty(followUpPrompt) ? "" : $"\n\n{followUpPrompt}");
                }

                // Check for cancel/restart intent
                if (Regex.IsMatch(msg, @"\b(cancel|stop|quit|restart|start over)\b", RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation("Cancel/restart intent detected.");
                    userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : null;
                    message.BotResponse = "Application process cancelled. How can I assist you now? To apply for a job, let me know you're interested in job openings or upload your resume.";
                    sessionMessages.Add(message);
                    _chatDbService.SaveMessage(message);
                    SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                    ClearApplicationState();
                    return Json(new { response = message.BotResponse, model = modelUsed });
                }

                // Check for resume upload intent
                if (Regex.IsMatch(msg, @"\b(resume|cv|upload resume|upload cv|attach resume|attach cv)\b", RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation("Resume upload intent detected.");
                    response = "Please upload your resume (PDF or Word format) using the upload button below the input area.";
                    message.BotResponse = response;
                    sessionMessages.Add(message);
                    _chatDbService.SaveMessage(message);
                    return Json(new { response, model = modelUsed });
                }

                // Step 1: Greeting
                if (Regex.IsMatch(msg, @"\b(hi|hello|hey)\b", RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation("Greeting intent detected.");
                    ClearApplicationState();
                    response = "👋 Hello! I’m the official company chatbot. How can I assist you today? Ask about our services, products, job openings, or upload your resume to find suitable jobs!";
                }

                // Step 2: Continue ongoing interview
                else if (_chatDbService.GetLatestSession(userId) is InterviewSession session && !session.IsComplete && session.QuestionIndex < session.Questions.Count)
                {
                    _logger.LogInformation($"Continuing interview for {session.JobTitle}, question {session.QuestionIndex + 1}.");
                    if (Regex.IsMatch(msg, @"\b(when|end|finish|done)\b", RegexOptions.IgnoreCase))
                    {
                        int remainingQuestions = session.Questions.Count - session.QuestionIndex;
                        response = $"You have {remainingQuestions} question{(remainingQuestions != 1 ? "s" : "")} remaining in the interview for {session.JobTitle}.";
                    }
                    else if (_chatGPTService.IsCompanyRelated(msg))
                    {
                        response = await HandleCompanyQuery(msg, $"Continuing with your interview for {session.JobTitle}. ❓ Question {session.QuestionIndex + 1}: {session.Questions[session.QuestionIndex]}");
                    }
                    else
                    {
                        session.Answers.Add(msg);
                        session.QuestionIndex++;

                        if (session.QuestionIndex < session.Questions.Count)
                        {
                            response = $"❓ Question {session.QuestionIndex + 1}: {session.Questions[session.QuestionIndex]}";
                        }
                        else
                        {
                            session.IsComplete = true;
                            response = $"✅ Thank you for completing the interview for the position of {session.JobTitle}.\nOur team will review your responses.";
                            userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : null;
                            message.BotResponse = response;
                            sessionMessages.Add(message);
                            _chatDbService.SaveMessage(message);
                            _chatDbService.UpdateInterviewSession(session);
                            SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                            ClearApplicationState();
                            return Json(new { response, model = modelUsed });
                        }

                        _chatDbService.UpdateInterviewSession(session);
                    }
                }

                // Step 3: Handle user details collection for job application
                else if (HttpContext.Session.GetString("ApplicationState") is string appState)
                {
                    userDetails = userDetailsStr is not null
                        ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) ?? new UserDetails { UserId = userId }
                        : new UserDetails { UserId = userId };

                    var currentQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == appState);
                    if (currentQuestion == null)
                    {
                        response = "❌ Invalid application state. Please start the application process again.";
                        ClearApplicationState();
                        message.BotResponse = response;
                        sessionMessages.Add(message);
                        _chatDbService.SaveMessage(message);
                        SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                        return Json(new { response, model = modelUsed });
                    }

                    // Handle backtracking
                    if (Regex.IsMatch(msg, @"\b(back|previous|go back|change)\b", RegexOptions.IgnoreCase))
                    {
                        _logger.LogInformation($"Backtracking from {appState}.");
                        var prevQuestion = _preInterviewQuestions.TakeWhile(q => q.State != appState).LastOrDefault();
                        if (prevQuestion != null)
                        {
                            HttpContext.Session.SetString("ApplicationState", prevQuestion.State);
                            response = prevQuestion.Prompt;
                        }
                        else
                        {
                            ClearApplicationState();
                            HttpContext.Session.SetString("JobList", HttpContext.Session.GetString("JobList") ?? "");
                            response = "Going back to job selection. Please reply with the job title or number you'd like to apply for, or upload your resume to find suitable jobs.";
                        }
                        HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                    }
                    // Handle company-related queries during application
                    else if (_chatGPTService.IsCompanyRelated(msg))
                    {
                        _logger.LogInformation($"Company-related intent detected during {appState}.");
                        response = await HandleCompanyQuery(msg, currentQuestion.Prompt);
                    }
                    // Handle phone/email correction
                    else if (appState == "AwaitingEmail" && Regex.IsMatch(msg, @"\b(phone|mobile|number)\b", RegexOptions.IgnoreCase))
                    {
                        var contactQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == "AwaitingContact");
                        if (contactQuestion != null)
                        {
                            HttpContext.Session.SetString("ApplicationState", "AwaitingContact");
                            userDetails.Email = null;
                            HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                            response = contactQuestion.Prompt;
                        }
                    }
                    // Normal flow with validations
                    else
                    {
                        if (appState == "AwaitingName")
                        {
                            if (Regex.IsMatch(msg, @"\b(none|skip|no name)\b", RegexOptions.IgnoreCase))
                            {
                                response = currentQuestion.ErrorMessage;
                            }
                            else
                            {
                                string extractedName = ExtractName(msg);
                                if (!Regex.IsMatch(extractedName, currentQuestion.ValidationRegex, RegexOptions.IgnoreCase))
                                {
                                    response = currentQuestion.ErrorMessage;
                                }
                                else
                                {
                                    userDetails.Name = extractedName;
                                    HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                                    var nextQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == "AwaitingContact");
                                    if (nextQuestion != null)
                                    {
                                        HttpContext.Session.SetString("ApplicationState", nextQuestion.State);
                                        response = nextQuestion.Prompt;
                                    }
                                }
                            }
                        }
                        else if (appState == "AwaitingContact" && currentQuestion.SkipAllowed && msg.ToLower() == "skip")
                        {
                            var skipQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == currentQuestion.SkipToState);
                            if (skipQuestion != null)
                            {
                                HttpContext.Session.SetString("ApplicationState", skipQuestion.State);
                                response = skipQuestion.Prompt;
                            }
                        }
                        else if (appState == "AwaitingEmploymentStatus")
                        {
                            if (!Regex.IsMatch(msg, currentQuestion.ValidationRegex, RegexOptions.IgnoreCase))
                            {
                                response = currentQuestion.ErrorMessage;
                            }
                            else
                            {
                                userDetails.EmploymentStatus = Regex.IsMatch(msg, @"\b(yes|yep|yepp|yeah|sure)\b", RegexOptions.IgnoreCase) ? "yes" : "no";
                                HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                                var nextState = userDetails.EmploymentStatus == "yes"
                                    ? currentQuestion.ConditionalNextStates?["yes"]
                                    : currentQuestion.ConditionalNextStates?["no"];
                                var nextQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == nextState);
                                if (nextQuestion != null)
                                {
                                    HttpContext.Session.SetString("ApplicationState", nextQuestion.State);
                                    response = nextQuestion.Prompt;
                                }
                            }
                        }
                        //else if (appState == "AwaitingInterviewStart")
                        //{
                        //    if (Regex.IsMatch(msg, @"\b(yes|yep|yepp|yeah|sure|start)\b", RegexOptions.IgnoreCase))
                        //    {
                        //        var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
                        //        if (!string.IsNullOrEmpty(selectedJob))
                        //        {
                        //            var (questions, interviewModel) = await _chatGPTService.GenerateRandomInterviewQuestionsWithModelAsync(selectedJob, 5);
                        //            var newSession = new InterviewSession
                        //            {
                        //                UserId = userId,
                        //                JobTitle = selectedJob,
                        //                Questions = questions,
                        //                QuestionIndex = 0,
                        //                IsComplete = false,
                        //                TabSwitchCount = 0
                        //            };
                        //            _chatDbService.SaveInterviewSession(newSession);
                        //            response = $"🧪 Starting interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
                        //            modelUsed = interviewModel;
                        //            message.BotResponse = response;
                        //            sessionMessages.Add(message);
                        //            _chatDbService.SaveMessage(message);
                        //            SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                        //            ClearApplicationState();
                        //            return Json(new { response, model = modelUsed });
                        //        }
                        //        else
                        //        {
                        //            response = "❌ No job selected. Please start the application process again or upload your resume to find suitable jobs.";
                        //            message.BotResponse = response;
                        //            sessionMessages.Add(message);
                        //            _chatDbService.SaveMessage(message);
                        //            SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                        //            ClearApplicationState();
                        //            return Json(new { response, model = modelUsed });
                        //        }
                        //    }
                        //    else if (Regex.IsMatch(msg, @"\b(no|nope|nah|don't)\b", RegexOptions.IgnoreCase))
                        //    {
                        //        response = "Okay, the interview will not start. How can I assist you now? To apply for another job, let me know you're interested in job openings or upload your resume.";
                        //        message.BotResponse = response;
                        //        sessionMessages.Add(message);
                        //        _chatDbService.SaveMessage(message);
                        //        SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                        //        ClearApplicationState();
                        //        return Json(new { response, model = modelUsed });
                        //    }
                        //    else
                        //    {
                        //        response = currentQuestion.ErrorMessage;
                        //    }
                        //}

                        //// In the AwaitingInterviewStart state handling
                        //else if (appState == "AwaitingInterviewStart")
                        //{
                        //    if (Regex.IsMatch(msg, @"\b(yes|yep|yepp|yeah|sure|start)\b", RegexOptions.IgnoreCase))
                        //    {
                        //        var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
                        //        if (!string.IsNullOrEmpty(selectedJob))
                        //        {
                        //            var (questions, interviewModel) = await _chatGPTService.GenerateRandomInterviewQuestionsWithModelAsync(selectedJob);
                        //            var newSession = new InterviewSession
                        //            {
                        //                UserId = userId,
                        //                JobTitle = selectedJob,
                        //                Questions = questions,
                        //                QuestionIndex = 0,
                        //                IsComplete = false,
                        //                TabSwitchCount = 0
                        //            };
                        //            _chatDbService.SaveInterviewSession(newSession);
                        //            response = $"🧪 Starting interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
                        //            modelUsed = interviewModel;
                        //            message.BotResponse = response;
                        //            sessionMessages.Add(message);
                        //            _chatDbService.SaveMessage(message);
                        //            SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                        //            ClearApplicationState();
                        //            return Json(new { response, model = modelUsed });
                        //        }
                        //        else
                        //        {
                        //            response = "❌ No job selected. Please start the application process again or upload your resume to find suitable jobs.";
                        //            message.BotResponse = response;
                        //            sessionMessages.Add(message);
                        //            _chatDbService.SaveMessage(message);
                        //            SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                        //            ClearApplicationState();
                        //            return Json(new { response, model = modelUsed });
                        //        }
                        //    }
                        //    else if (Regex.IsMatch(msg, @"\b(no|nope|nah|don't)\b", RegexOptions.IgnoreCase))
                        //    {
                        //        response = "Okay, the interview will not start. How can I assist you now? To apply for another job, let me know you're interested in job openings or upload your resume.";
                        //        message.BotResponse = response;
                        //        sessionMessages.Add(message);
                        //        _chatDbService.SaveMessage(message);
                        //        SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                        //        ClearApplicationState();
                        //        return Json(new { response, model = modelUsed });
                        //    }
                        //    else
                        //    {
                        //        response = currentQuestion.ErrorMessage;
                        //    }
                        //}

                        // In the AwaitingInterviewStart block, replace the existing code with:
                        else if (appState == "AwaitingInterviewStart")
                        {
                            if (Regex.IsMatch(msg, @"\b(yes|yep|yepp|yeah|sure|start)\b", RegexOptions.IgnoreCase))
                            {
                                var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
                                if (!string.IsNullOrEmpty(selectedJob))
                                {
                                    // Check if webcam consent was granted (set in session after granting access)
                                    if (HttpContext.Session.GetString("WebcamConsent") != "granted")
                                    {
                                        response = "Webcam access is required to start the interview. Please grant webcam access.";
                                        message.BotResponse = response;
                                        sessionMessages.Add(message);
                                        _chatDbService.SaveMessage(message);
                                        return Json(new { response, model = modelUsed });
                                    }

                                    var (questions, interviewModel) = await _chatGPTService.GenerateRandomInterviewQuestionsWithModelAsync(selectedJob);
                                    var newSession = new InterviewSession
                                    {
                                        UserId = userId,
                                        JobTitle = selectedJob,
                                        Questions = questions,
                                        QuestionIndex = 0,
                                        IsComplete = false,
                                        TabSwitchCount = 0
                                    };
                                    _chatDbService.SaveInterviewSession(newSession);
                                    response = $"🧪 Starting interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
                                    modelUsed = interviewModel;
                                    message.BotResponse = response;
                                    sessionMessages.Add(message);
                                    _chatDbService.SaveMessage(message);
                                    SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                                    ClearApplicationState();
                                    return Json(new { response, model = modelUsed });
                                }
                                else
                                {
                                    response = "❌ No job selected. Please start the application process again or upload your resume to find suitable jobs.";
                                    message.BotResponse = response;
                                    sessionMessages.Add(message);
                                    _chatDbService.SaveMessage(message);
                                    SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                                    ClearApplicationState();
                                    return Json(new { response, model = modelUsed });
                                }
                            }
                            else if (Regex.IsMatch(msg, @"\b(no|nope|nah|don't)\b", RegexOptions.IgnoreCase))
                            {
                                response = "Okay, the interview will not start. How can I assist you now? To apply for another job, let me know you're interested in job openings or upload your resume.";
                                message.BotResponse = response;
                                sessionMessages.Add(message);
                                _chatDbService.SaveMessage(message);
                                SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                                ClearApplicationState();
                                return Json(new { response, model = modelUsed });
                            }
                            else
                            {
                                response = currentQuestion.ErrorMessage;
                            }
                        }
                        else
                        {
                            if (!Regex.IsMatch(msg, currentQuestion.ValidationRegex, RegexOptions.IgnoreCase))
                            {
                                response = currentQuestion.ErrorMessage;
                            }
                            else
                            {
                                if (appState == "AwaitingContact")
                                    userDetails.Phone = msg;
                                else if (appState == "AwaitingEmail")
                                    userDetails.Email = msg;
                                else if (appState == "AwaitingExperience")
                                    userDetails.Experience = msg;
                                else if (appState == "AwaitingReasonForLeaving" || appState == "AwaitingReasonToJoin")
                                    userDetails.Reason = string.IsNullOrEmpty(userDetails.Reason) ? msg : userDetails.Reason + "; " + msg;

                                HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                                var nextQuestion = _preInterviewQuestions.SkipWhile(q => q.State != appState).Skip(1).FirstOrDefault();
                                if (appState == "AwaitingReasonToJoin")
                                {
                                    userDetails.CreatedAt = DateTime.Now;
                                    _chatDbService.SaveUserDetails(userDetails);
                                }
                                if (nextQuestion != null)
                                {
                                    HttpContext.Session.SetString("ApplicationState", nextQuestion.State);
                                    response = nextQuestion.Prompt;
                                }
                            }
                        }
                    }

                    HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                }

                // Step 4: Handle job selection
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
                        HttpContext.Session.SetString("SelectedJob", selectedJob);
                        HttpContext.Session.SetString("ApplicationState", _preInterviewQuestions.FirstOrDefault()?.State ?? "AwaitingName");
                        HttpContext.Session.Remove("JobList");
                        response = $"You’ve selected {selectedJob}. {_preInterviewQuestions.FirstOrDefault()?.Prompt}";
                    }
                    else
                    {
                        response = await HandleCompanyQuery(msg, "Please reply with the job title or number you'd like to apply for, or upload your resume to find suitable jobs.");
                    }
                }

                //// Step 5: Detect job intent
                //else if (await _chatGPTService.IsJobIntentAsync(msg))
                //{
                //    _logger.LogInformation("Job intent detected.");
                //    var (jobList, jobModel) = await _chatGPTService.GetJobOpeningsAsync();
                //    modelUsed = jobModel;

                //    if (jobList.Count > 0)
                //    {
                //        response = "🧑‍💻 Current job openings:\n";
                //        for (int i = 0; i < jobList.Count; i++)
                //            response += $"{i + 1}. {jobList[i]}\n";

                //        response += "\nPlease reply with the job title or number you'd like to apply for, or upload your resume to find suitable jobs.";
                //        HttpContext.Session.SetString("JobList", string.Join("||", jobList));
                //    }
                //    else
                //    {
                //        response = "❌ Sorry, no job openings found at the moment. You can upload your resume to check for matching jobs.";
                //        _logger.LogWarning("No job openings available or API returned empty list.");
                //    }
                //}

                //// Step 6: Handle location-related queries
                //else if (await _chatGPTService.IsLocationIntentAsync(msg) || _chatGPTService.IsLocationRelated(msg))
                //{
                //    _logger.LogInformation("Location-related intent detected.");
                //    var (resp, model) = await _chatGPTService.GetSmartResponseAsync(msg);
                //    modelUsed = model;
                //    response = resp;
                //}

                //// Step 7: Handle company-related queries
                //else if (_chatGPTService.IsCompanyRelated(msg))
                //{
                //    _logger.LogInformation("Company-related intent detected.");
                //    HttpContext.Session.Remove("JobList");
                //    HttpContext.Session.Remove("ApplicationState");
                //    response = await HandleCompanyQuery(msg);
                //}

                // Step 5: Detect job intent
                else if (await _chatGPTService.IsJobIntentAsync(msg))
                {
                    _logger.LogInformation("Job intent detected for query: {msg}. Fetching job openings.", msg);
                    var (jobList, jobModel) = await _chatGPTService.GetJobOpeningsAsync();
                    modelUsed = jobModel;

                    if (jobList.Count > 0)
                    {
                        response = "🧑‍💻 Current job openings:\n";
                        for (int i = 0; i < jobList.Count; i++)
                            response += $"{i + 1}. {jobList[i]}\n";

                        response += "\nPlease reply with the job title or number you'd like to apply for, or upload your resume to find suitable jobs.";
                        HttpContext.Session.SetString("JobList", string.Join("||", jobList));
                        _logger.LogInformation("Job list generated: {jobList}", string.Join(", ", jobList));
                    }
                    else
                    {
                        response = "❌ Sorry, no job openings found at the moment. You can upload your resume to check for matching jobs.";
                        _logger.LogWarning("No job openings available or API returned empty list.");
                    }
                }
                // Step 6: Handle location-related queries
                else if (await _chatGPTService.IsLocationIntentAsync(msg) || _chatGPTService.IsLocationRelated(msg))
                {
                    _logger.LogInformation("Location-related intent detected for query: {msg}.", msg);
                    var (resp, model) = await _chatGPTService.GetSmartResponseAsync(msg);
                    modelUsed = model;
                    response = resp;
                }
                // Step 7: Handle company-related queries
                else if (_chatGPTService.IsCompanyRelated(msg))
                {
                    _logger.LogInformation("Company-related intent detected for query: {msg}.", msg);
                    HttpContext.Session.Remove("JobList");
                    HttpContext.Session.Remove("ApplicationState");
                    response = await HandleCompanyQuery(msg);
                }

                // Step 8: Fallback to AI-based response
                else
                {
                    _logger.LogWarning($"No specific intent detected for query: {msg}. Attempting to process with GetSmartResponseAsync.");
                    var (resp, model) = await _chatGPTService.GetSmartResponseAsync(msg);
                    modelUsed = model;
                    response = resp;
                }

                message.BotResponse = response;
                message.Model = modelUsed;
                sessionMessages.Add(message);
                HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(sessionMessages));
                _chatDbService.SaveMessage(message);
                userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : null;
                SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);

                return Json(new { response, model = modelUsed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing query: {msg}");
                response = "❌ Unexpected error occurred. Please try again or contact us.";
                modelUsed = "error";
                message.BotResponse = response;
                sessionMessages.Add(message);
                HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(sessionMessages));
                _chatDbService.SaveMessage(message);
                return Json(new { response, model = modelUsed });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UploadResume(IFormFile resume)
        {
            string userId = HttpContext.Session.Id;
            string response = "";
            string modelUsed = "custom";

            try
            {
                if (resume == null || resume.Length == 0)
                {
                    response = "❌ No file uploaded. Please upload a PDF or Word document.";
                    return Json(new { response, model = modelUsed });
                }

                if (resume.Length > 5 * 1024 * 1024) // 5MB limit
                {
                    response = "❌ File size exceeds 5MB. Please upload a smaller file.";
                    return Json(new { response, model = modelUsed });
                }

                var extension = Path.GetExtension(resume.FileName).ToLower();
                if (extension != ".pdf" && extension != ".docx")
                {
                    response = "❌ Invalid file format. Please upload a PDF or Word (.docx) document.";
                    return Json(new { response, model = modelUsed });
                }

                string resumeText;
                using (var stream = new MemoryStream())
                {
                    await resume.CopyToAsync(stream);
                    stream.Position = 0;

                    if (extension == ".pdf")
                    {
                        using (var reader = new PdfReader(stream))
                        using (var pdfDoc = new PdfDocument(reader))
                        {
                            var text = new StringBuilder();
                            for (int i = 1; i <= pdfDoc.GetNumberOfPages(); i++)
                            {
                                text.Append(PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(i)));
                            }
                            resumeText = text.ToString();
                        }
                    }
                    else // .docx
                    {
                        using (var doc = DocX.Load(stream))
                        {
                            resumeText = doc.Text;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(resumeText))
                {
                    response = "❌ Unable to extract content from the resume. Please ensure the file contains readable text.";
                    return Json(new { response, model = modelUsed });
                }

                HttpContext.Session.SetString("ResumeContent", resumeText);

                var (matchingJobs, jobModel) = await _chatGPTService.FindJobsByResumeAsync(resumeText);
                modelUsed = jobModel;

                if (matchingJobs.Count > 0)
                {
                    response = "🧑‍💻 Based on your resume, here are the matching job openings:\n";
                    for (int i = 0; i < matchingJobs.Count; i++)
                        response += $"{i + 1}. {matchingJobs[i]}\n";

                    response += "\nPlease reply with the job title or number you'd like to apply for.";
                    HttpContext.Session.SetString("JobList", string.Join("||", matchingJobs));
                }
                else
                {
                    response = "❌ No matching job openings found for your resume. You can try asking about other job opportunities or contact us for more information.";
                }

                var message = new ChatMessage
                {
                    UserMessage = "Uploaded resume",
                    BotResponse = response,
                    Model = modelUsed,
                    CreatedAt = DateTime.Now
                };
                var sessionMessages = HttpContext.Session.GetString("SessionMessages") is string messagesStr
                    ? JsonConvert.DeserializeObject<List<ChatMessage>>(messagesStr) ?? new List<ChatMessage>()
                    : new List<ChatMessage>();
                sessionMessages.Add(message);
                _chatDbService.SaveMessage(message);
                HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(sessionMessages));

                return Json(new { response, model = modelUsed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing resume upload.");
                response = "❌ Error processing your resume. Please try again or contact us.";
                return Json(new { response, model = "error" });
            }
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

        [HttpPost]
        public async Task<IActionResult> UploadSnapshot(IFormFile snapshot)
        {
            string userId = HttpContext.Session.Id;
            string response = "";
            string modelUsed = "custom";

            try
            {
                if (snapshot == null || snapshot.Length == 0)
                {
                    response = "❌ No snapshot uploaded.";
                    return Json(new { response, model = modelUsed });
                }

                if (snapshot.Length > 2 * 1024 * 1024) // 2MB limit
                {
                    response = "❌ Snapshot size exceeds 2MB.";
                    return Json(new { response, model = modelUsed });
                }

                var extension = Path.GetExtension(snapshot.FileName).ToLower();
                if (extension != ".jpg" && extension != ".jpeg")
                {
                    response = "❌ Invalid snapshot format. Only JPEG images are allowed.";
                    return Json(new { response, model = modelUsed });
                }

                // Get the latest interview session
                var session = _chatDbService.GetLatestSession(userId);
                if (session == null || session.IsComplete)
                {
                    response = "❌ No active interview session found.";
                    return Json(new { response, model = modelUsed });
                }

                // Save snapshot to file system
                var folderPath = @"C:\Conversation\Snapshots";
                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                var fileName = $"snapshot_{userId}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                var filePath = Path.Combine(folderPath, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await snapshot.CopyToAsync(stream);
                }

                // Save snapshot metadata to database
                _chatDbService.SaveSnapshot(userId, session.Id, filePath);

                response = "Snapshot uploaded successfully.";
                return Json(new { response, model = modelUsed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing snapshot upload.");
                response = "❌ Error processing snapshot.";
                return Json(new { response, model = "error" });
            }
        }

        [HttpPost]
        public IActionResult SetWebcamConsent([FromBody] WebcamConsentModel model)
        {
            HttpContext.Session.SetString("WebcamConsent", model.Consent);
            return Ok();
        }

        public class WebcamConsentModel
        {
            public string Consent { get; set; }
        }
    }
}