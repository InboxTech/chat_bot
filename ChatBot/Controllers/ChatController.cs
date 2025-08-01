using ChatBot.Models;
using ChatBot.Services;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xceed.Words.NET;

namespace ChatBot.Controllers
{
    public class ChatController : Controller
    {
        private readonly ChatGPTService _chatGPTService;
        private readonly ChatDbService _chatDbService;
        private readonly ILogger<ChatController> _logger;
        private readonly IConfiguration _configuration;
        private readonly List<PreInterviewQuestion> _preInterviewQuestions;
        private readonly string _resumeFolder;
        private readonly string _idProofFolder;
        private readonly string _interviewVideoFolder;

        public ChatController(
            ChatGPTService chatGPTService,
            ChatDbService chatDbService,
            ILogger<ChatController> logger,
            IConfiguration configuration)
        {
            _chatGPTService = chatGPTService;
            _chatDbService = chatDbService;
            _logger = logger;
            _configuration = configuration;
            _preInterviewQuestions = configuration.GetSection("PreInterviewQuestions")
                                                 .Get<List<PreInterviewQuestion>>();
            _resumeFolder = configuration.GetSection("UploadPaths:ResumeFolder").Value;
            _idProofFolder = configuration.GetSection("UploadPaths:IDProofFolder").Value;
            _interviewVideoFolder = configuration.GetSection("UploadPaths:InterviewVideoFolder").Value;
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
            public bool RequiresIDProof { get; set; }
        }

        [HttpGet]
        public IActionResult Index()
        {
            HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(new List<ChatMessage>()));
            HttpContext.Session.SetString("UserIdentity", "");
            EnsureUserRecord(HttpContext.Session.Id);
            return View();
        }

        private void EnsureUserRecord(string userId)
        {
            try
            {
                var connectionString = _configuration.GetConnectionString("DefaultConnection");
                using var conn = new SqlConnection(connectionString);
                conn.Open();

                var checkCmd = new SqlCommand("SELECT COUNT(*) FROM Users WHERE UserId = @UserId", conn);
                checkCmd.Parameters.AddWithValue("@UserId", userId);
                bool userExists = (int)checkCmd.ExecuteScalar() > 0;

                if (!userExists)
                {
                    var user = new UserDetails
                    {
                        UserId = userId,
                        CreatedAt = DateTime.Now
                    };
                    _chatDbService.SaveUserDetails(user);
                    _logger.LogInformation("Created new Users record for UserId: {UserId}", userId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ensuring Users record for UserId: {UserId}", userId);
                throw;
            }
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage([FromBody] ChatMessage userMsg)
        {
            string msg = userMsg.UserMessage?.Trim() ?? "";
            string userId = HttpContext.Session.Id;
            string response = "";
            string modelUsed = "custom";
            bool startInterview = false;

            EnsureUserRecord(userId);

            var message = new ChatMessage
            {
                UserId = userId,
                UserMessage = msg,
                CreatedAt = DateTime.Now
            };

            var sessionMessages = HttpContext.Session.GetString("SessionMessages") is string messagesStr
                ? JsonConvert.DeserializeObject<List<ChatMessage>>(messagesStr) ?? new List<ChatMessage>()
                : new List<ChatMessage>();
            var userIdentity = HttpContext.Session.GetString("UserIdentity");

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

                void ClearApplicationState()
                {
                    HttpContext.Session.Remove("ApplicationState");
                    HttpContext.Session.Remove("UserDetails");
                    HttpContext.Session.Remove("SelectedJob");
                    HttpContext.Session.Remove("JobList");
                    HttpContext.Session.Remove("ResumeContent");
                    HttpContext.Session.Remove("InterviewRetakeCount");
                    HttpContext.Session.Remove("FirstInterviewSessionId");
                    HttpContext.Session.Remove("RetakeInterviewSessionId");
                }

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

                if (Regex.IsMatch(msg, @"\b(cancel|stop|quit|restart|start over)\b", RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation("Cancel/restart intent detected.");
                    userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : null;
                    message.BotResponse = "Application process cancelled. How can I assist you now? To apply for a job, let me know you're interested in job openings or upload your resume.";
                    sessionMessages.Add(message);
                    _chatDbService.SaveMessage(message);
                    SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                    ClearApplicationState();
                    return Json(new { response = message.BotResponse, model = modelUsed, startInterview = false });
                }

                if (Regex.IsMatch(msg, @"\b(resume|cv|upload resume|upload cv|attach resume|attach cv)\b", RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation("Resume upload intent detected.");
                    response = "Please upload your resume (PDF or Word format) using the upload button below the input area.";
                    message.BotResponse = response;
                    sessionMessages.Add(message);
                    _chatDbService.SaveMessage(message);
                    return Json(new { response, model = modelUsed, startInterview = false });
                }

                if (Regex.IsMatch(msg, @"\b(hi|hello|hey)\b", RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation("Greeting intent detected.");
                    ClearApplicationState();
                    response = "👋 Hello! I’m the official company chatbot. How can I assist you today? Ask about our services, products, job openings, or upload your resume to find suitable jobs!";
                }
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
                            int retakeCount = HttpContext.Session.GetInt32("InterviewRetakeCount") ?? 0;
                            if (retakeCount == 0)
                            {
                                HttpContext.Session.SetString("FirstInterviewSessionId", session.Id.ToString());
                                response = $"✅ Thank you for completing the interview for the position of {session.JobTitle}. Would you like to retake the interview? You have one opportunity to retake. Reply 'retake' to start over or 'submit' to finalize your interview.";
                                HttpContext.Session.SetInt32("InterviewRetakeCount", retakeCount + 1);
                            }
                            else
                            {
                                HttpContext.Session.SetString("RetakeInterviewSessionId", session.Id.ToString());
                                response = $"✅ Thank you for completing your retake interview for the position of {session.JobTitle}. Please choose which interview to submit: reply 'first' to submit your first attempt or 'retake' to submit this retake.";
                            }
                            _chatDbService.UpdateInterviewSession(session);
                        }

                        _chatDbService.UpdateInterviewSession(session);
                    }
                }
                else if (HttpContext.Session.GetString("FirstInterviewSessionId") is string firstSessionId && HttpContext.Session.GetString("RetakeInterviewSessionId") is string retakeSessionId)
                {
                    if (Regex.IsMatch(msg, @"\b(first)\b", RegexOptions.IgnoreCase))
                    {
                        response = $"✅ Your first interview for the position has been submitted. Our team will review your responses.";
                        _chatDbService.MarkInterviewAsSubmitted(int.Parse(firstSessionId));
                        ClearApplicationState();
                    }
                    else if (Regex.IsMatch(msg, @"\b(retake)\b", RegexOptions.IgnoreCase))
                    {
                        response = $"✅ Your retake interview for the position has been submitted. Our team will review your responses.";
                        _chatDbService.MarkInterviewAsSubmitted(int.Parse(retakeSessionId));
                        ClearApplicationState();
                    }
                    else
                    {
                        response = "Please reply with 'first' to submit your first interview attempt or 'retake' to submit your retake interview.";
                    }
                }
                else if (HttpContext.Session.GetString("FirstInterviewSessionId") is string && Regex.IsMatch(msg, @"\b(submit)\b", RegexOptions.IgnoreCase))
                {
                    response = $"✅ Your interview for the position has been submitted. Our team will review your responses.";
                    _chatDbService.MarkInterviewAsSubmitted(int.Parse(HttpContext.Session.GetString("FirstInterviewSessionId")));
                    ClearApplicationState();
                }
                else if (HttpContext.Session.GetString("FirstInterviewSessionId") is string _ && Regex.IsMatch(msg, @"\b(retake)\b", RegexOptions.IgnoreCase))
                {
                    int retakeCount = HttpContext.Session.GetInt32("InterviewRetakeCount") ?? 0;
                    if (retakeCount >= 1)
                    {
                        var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
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
                        response = $"🧪 Starting retake interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
                        startInterview = true;
                        modelUsed = interviewModel;
                    }
                }
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
                        return Json(new { response, model = modelUsed, startInterview = false });
                    }

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
                    else if (_chatGPTService.IsCompanyRelated(msg))
                    {
                        _logger.LogInformation($"Company-related intent detected during {appState}.");
                        response = await HandleCompanyQuery(msg, currentQuestion.Prompt);
                    }
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
                        else if (appState == "AwaitingIDProof")
                        {
                            response = currentQuestion.Prompt;
                            message.BotResponse = response;
                            sessionMessages.Add(message);
                            _chatDbService.SaveMessage(message);
                            return Json(new { response, model = modelUsed, startInterview = false });
                        }
                        else if (appState == "AwaitingInterviewStart")
                        {
                            if (Regex.IsMatch(msg, @"\b(yes|yep|yepp|yeah|sure|start)\b", RegexOptions.IgnoreCase))
                            {
                                int attemptCount = _chatDbService.GetInterviewAttemptCount(userDetails.Name, userDetails.Email, userDetails.Phone, null);
                                if (attemptCount >= 1)
                                {
                                    response = "❌ You have already attempted the interview for this position. Only one interview attempt is allowed.";
                                    ClearApplicationState();
                                    message.BotResponse = response;
                                    sessionMessages.Add(message);
                                    _chatDbService.SaveMessage(message);
                                    SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                                    return Json(new { response, model = modelUsed, startInterview = false });
                                }

                                if (string.IsNullOrEmpty(userDetails.IDProofPath))
                                {
                                    var idProofQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == "AwaitingIDProof");
                                    if (idProofQuestion != null)
                                    {
                                        HttpContext.Session.SetString("ApplicationState", idProofQuestion.State);
                                        response = idProofQuestion.Prompt;
                                        message.BotResponse = response;
                                        sessionMessages.Add(message);
                                        _chatDbService.SaveMessage(message);
                                        return Json(new { response, model = modelUsed, startInterview = false });
                                    }
                                }

                                var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
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
                                startInterview = true;
                                modelUsed = interviewModel;
                            }
                            else if (Regex.IsMatch(msg, @"\b(no|nope|nah|don't)\b", RegexOptions.IgnoreCase))
                            {
                                response = "Okay, the interview will not start. How can I assist you now? To apply for another job, let me know you're interested in job openings or upload your resume.";
                                message.BotResponse = response;
                                sessionMessages.Add(message);
                                _chatDbService.SaveMessage(message);
                                SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                                ClearApplicationState();
                                return Json(new { response, model = modelUsed, startInterview = false });
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
                                var nextQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == (appState == "AwaitingReasonToJoin" ? "AwaitingIDProof" : _preInterviewQuestions.SkipWhile(q => q.State != appState).Skip(1).FirstOrDefault()?.State));
                                if (appState == "AwaitingReasonToJoin")
                                {
                                    try
                                    {
                                        userDetails.CreatedAt = DateTime.Now;
                                        _chatDbService.SaveUserDetails(userDetails);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "Failed to save user details in AwaitingReasonToJoin for UserId: {UserId}", userDetails.UserId);
                                        throw;
                                    }
                                }
                                if (nextQuestion != null)
                                {
                                    HttpContext.Session.SetString("ApplicationState", nextQuestion.State);
                                    response = nextQuestion.Prompt;
                                }
                            }
                        }

                        HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                    }
                }
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
                else if (await _chatGPTService.IsLocationIntentAsync(msg) || _chatGPTService.IsLocationRelated(msg))
                {
                    _logger.LogInformation("Location-related intent detected for query: {msg}.", msg);
                    var (resp, model) = await _chatGPTService.GetSmartResponseAsync(msg);
                    modelUsed = model;
                    response = resp;
                }
                else if (_chatGPTService.IsCompanyRelated(msg))
                {
                    _logger.LogInformation("Company-related intent detected for query: {msg}.", msg);
                    HttpContext.Session.Remove("JobList");
                    HttpContext.Session.Remove("ApplicationState");
                    response = await HandleCompanyQuery(msg);
                }
                else
                {
                    _logger.LogInformation($"No specific intent detected for query: {msg}. Attempting to process with GetSmartResponseAsync.");
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

                return Json(new { response, model = modelUsed, startInterview });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing query: {msg} in state: {HttpContext.Session.GetString("ApplicationState")}");
                response = "❌ Unexpected error occurred. Please try again or contact us.";
                modelUsed = "error";
                message.BotResponse = response;
                sessionMessages.Add(message);
                HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(sessionMessages));
                _chatDbService.SaveMessage(message);
                return Json(new { response, model = modelUsed, startInterview = false });
            }
        }

        //[HttpPost]
        //public async Task<IActionResult> UploadResume(IFormFile resume)
        //{
        //    string userId = HttpContext.Session.Id;
        //    string response = "";
        //    string modelUsed = "custom";

        //    EnsureUserRecord(userId);

        //    try
        //    {
        //        if (resume == null || resume.Length == 0)
        //        {
        //            response = "❌ No file uploaded. Please upload a PDF or Word document.";
        //            return Json(new { response, model = modelUsed });
        //        }

        //        if (resume.Length > 5 * 1024 * 1024) // 5MB limit
        //        {
        //            response = "❌ File size exceeds 5MB. Please upload a smaller file.";
        //            return Json(new { response, model = modelUsed });
        //        }

        //        var extension = Path.GetExtension(resume.FileName).ToLower();
        //        if (extension != ".pdf" && extension != ".docx")
        //        {
        //            response = "❌ Invalid file format. Please upload a PDF or Word (.docx) document.";
        //            return Json(new { response, model = modelUsed });
        //        }

        //        string resumeText;
        //        using (var stream = new MemoryStream())
        //        {
        //            await resume.CopyToAsync(stream);
        //            stream.Position = 0;

        //            if (extension == ".pdf")
        //            {
        //                using (var reader = new PdfReader(stream))
        //                using (var pdfDoc = new PdfDocument(reader))
        //                {
        //                    var text = new StringBuilder();
        //                    for (int i = 1; i <= pdfDoc.GetNumberOfPages(); i++)
        //                    {
        //                        text.Append(PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(i)));
        //                    }
        //                    resumeText = text.ToString();
        //                }
        //            }
        //            else // .docx
        //            {
        //                using (var doc = DocX.Load(stream))
        //                {
        //                    resumeText = doc.Text;
        //                }
        //            }
        //        }

        //        if (string.IsNullOrWhiteSpace(resumeText))
        //        {
        //            response = "❌ Unable to extract content from the resume. Please ensure the file contains readable text.";
        //            return Json(new { response, model = modelUsed });
        //        }

        //        HttpContext.Session.SetString("ResumeContent", resumeText);

        //        var (matchingJobs, jobModel) = await _chatGPTService.FindJobsByResumeAsync(resumeText);
        //        modelUsed = jobModel;

        //        if (matchingJobs.Count > 0)
        //        {
        //            response = "🧑‍💻 Based on your resume, here are the matching job openings:\n";
        //            for (int i = 0; i < matchingJobs.Count; i++)
        //                response += $"{i + 1}. {matchingJobs[i]}\n";

        //            response += "\nPlease reply with the job title or number you'd like to apply for.";
        //            HttpContext.Session.SetString("JobList", string.Join("||", matchingJobs));
        //        }
        //        else
        //        {
        //            response = "❌ No matching job openings found for your resume. You can try asking about other job opportunities or contact us for more information.";
        //        }

        //        var message = new ChatMessage
        //        {
        //            UserId = userId,
        //            UserMessage = "Uploaded resume",
        //            BotResponse = response,
        //            Model = modelUsed,
        //            CreatedAt = DateTime.Now
        //        };
        //        var sessionMessages = HttpContext.Session.GetString("SessionMessages") is string messagesStr
        //            ? JsonConvert.DeserializeObject<List<ChatMessage>>(messagesStr) ?? new List<ChatMessage>()
        //            : new List<ChatMessage>();
        //        sessionMessages.Add(message);
        //        _chatDbService.SaveMessage(message);
        //        HttpContext.Session.SetString("SessionMessages", JsonConvert.SerializeObject(sessionMessages));

        //        return Json(new { response, model = modelUsed });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error processing resume upload.");
        //        response = "❌ Error processing your resume. Please try again or contact us.";
        //        return Json(new { response, model = "error" });
        //    }
        //}

        //[HttpPost]
        //public async Task<IActionResult> UploadIDProof()
        //{
        //    var userId = HttpContext.Session.Id;
        //    var userDetailsStr = HttpContext.Session.GetString("UserDetails");
        //    var userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : new UserDetails { UserId = userId };
        //    var chatMessage = new ChatMessage
        //    {
        //        UserId = userId,
        //        UserMessage = "Uploaded ID proof",
        //        Model = "custom",
        //        CreatedAt = DateTime.Now
        //    };
        //    string response = "";

        //    EnsureUserRecord(userId);

        //    try
        //    {
        //        var file = Request.Form.Files["idProof"];
        //        if (file == null || file.Length == 0)
        //        {
        //            response = "No ID proof uploaded. Please upload a JPG, PNG, or PDF of your government-issued ID (e.g., passport, driver's license).";
        //            chatMessage.BotResponse = response;
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = false, message = response, reason = "no_file", model = "custom", startInterview = false });
        //        }

        //        if (file.Length > 5 * 1024 * 1024)
        //        {
        //            response = "File size exceeds 5MB. Please upload a smaller file.";
        //            chatMessage.BotResponse = response;
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = false, message = response, reason = "file_too_large", model = "custom", startInterview = false });
        //        }

        //        var extension = Path.GetExtension(file.FileName).ToLower();
        //        if (extension != ".jpg" && extension != ".jpeg" && extension != ".png" && extension != ".pdf")
        //        {
        //            response = "Invalid file format. Please upload a JPG, PNG, or PDF file.";
        //            chatMessage.BotResponse = response;
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = false, message = response, reason = "invalid_format", model = "custom", startInterview = false });
        //        }

        //        int retryCount = HttpContext.Session.GetInt32("IDProofRetryCount") ?? 0;
        //        if (retryCount >= 3)
        //        {
        //            response = "You have exceeded the maximum number of ID proof upload attempts (3). Please contact support or start the application process again.";
        //            chatMessage.BotResponse = response;
        //            _chatDbService.SaveMessage(chatMessage);
        //            HttpContext.Session.Remove("ApplicationState");
        //            HttpContext.Session.Remove("SelectedJob");
        //            return Json(new { success = false, message = response, reason = "retry_limit_exceeded", model = "custom", startInterview = false });
        //        }

        //        int attemptCount = _chatDbService.GetInterviewAttemptCount(userDetails.Name, userDetails.Email, userDetails.Phone, null);
        //        if (attemptCount >= 1)
        //        {
        //            response = "❌ You have already attempted the interview for this position. Only one interview attempt is allowed.";
        //            chatMessage.BotResponse = response;
        //            _chatDbService.SaveMessage(chatMessage);
        //            HttpContext.Session.Remove("ApplicationState");
        //            HttpContext.Session.Remove("SelectedJob");
        //            return Json(new { success = false, message = response, reason = "interview_limit_exceeded", model = "custom", startInterview = false });
        //        }

        //        var userFolderName = string.IsNullOrEmpty(userDetails.Name) ? userId : userDetails.Name.Replace(" ", "_");
        //        var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "Uploads", "IDProofs", userFolderName);
        //        if (!Directory.Exists(uploadsFolder))
        //        {
        //            Directory.CreateDirectory(uploadsFolder);
        //        }

        //        var fileName = $"{userFolderName}_{Guid.NewGuid()}{extension}";
        //        var filePath = Path.Combine(uploadsFolder, fileName);

        //        using (var stream = new FileStream(filePath, FileMode.Create))
        //        {
        //            await file.CopyToAsync(stream);
        //        }

        //        userDetails.IDProofPath = filePath;
        //        userDetails.IDProofType = extension == ".pdf" ? "PDF Document" : "Image";
        //        HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
        //        _chatDbService.SaveUserDetails(userDetails);

        //        HttpContext.Session.SetInt32("IDProofRetryCount", 0);
        //        response = $"ID proof uploaded and saved: {fileName}";
        //        chatMessage.BotResponse = response;
        //        _chatDbService.SaveMessage(chatMessage);

        //        var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
        //        if (!string.IsNullOrEmpty(selectedJob))
        //        {
        //            var nextQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == "AwaitingInterviewStart");
        //            if (nextQuestion != null)
        //            {
        //                HttpContext.Session.SetString("ApplicationState", nextQuestion.State);
        //                response = nextQuestion.Prompt;
        //                chatMessage.BotResponse = response;
        //                _chatDbService.SaveMessage(chatMessage);
        //                return Json(new { success = true, response, model = "custom", startInterview = false });
        //            }
        //            else
        //            {
        //                response = "❌ Invalid application state after ID upload. Please start the application process again.";
        //                chatMessage.BotResponse = response;
        //                _chatDbService.SaveMessage(chatMessage);
        //                return Json(new { success = false, message = response, reason = "invalid_state", model = "custom", startInterview = false });
        //            }
        //        }
        //        else
        //        {
        //            response = "❌ No job selected. Please start the application process again or upload your resume to find suitable jobs.";
        //            chatMessage.BotResponse = response;
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = false, message = response, reason = "no_job_selected", model = "custom", startInterview = false });
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error uploading ID proof.");
        //        response = "Error uploading ID proof. Please try again or contact support.";
        //        chatMessage.BotResponse = response;
        //        _chatDbService.SaveMessage(chatMessage);
        //        return Json(new { success = false, message = response, reason = "exception", model = "error", startInterview = false });
        //    }
        //}

        //[HttpPost]
        //public async Task<IActionResult> UploadInterviewVideo()
        //{
        //    try
        //    {
        //        var file = Request.Form.Files["video"];
        //        if (file == null || file.Length == 0)
        //        {
        //            return BadRequest("No video file uploaded.");
        //        }

        //        var userId = HttpContext.Session.Id;
        //        var userDetailsStr = HttpContext.Session.GetString("UserDetails");
        //        var userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : new UserDetails { UserId = userId };

        //        var userFolderName = string.IsNullOrEmpty(userDetails.Name) ? userId : userDetails.Name.Replace(" ", "_");
        //        var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "Uploads", "InterviewVideos", userFolderName);
        //        if (!Directory.Exists(uploadsFolder))
        //        {
        //            Directory.CreateDirectory(uploadsFolder);
        //        }

        //        var fileName = $"{userFolderName}_{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
        //        var filePath = Path.Combine(uploadsFolder, fileName);

        //        using (var stream = new FileStream(filePath, FileMode.Create))
        //        {
        //            await file.CopyToAsync(stream);
        //        }

        //        var session = _chatDbService.GetLatestSession(userId);
        //        if (session != null)
        //        {
        //            session.VideoPath = filePath;
        //            _chatDbService.UpdateInterviewSession(session);
        //        }

        //        var chatMessage = new ChatMessage
        //        {
        //            UserId = userId,
        //            BotResponse = $"Video recorded and saved: {fileName}",
        //            CreatedAt = DateTime.Now,
        //            Model = "custom"
        //        };
        //        _chatDbService.SaveMessage(chatMessage);

        //        return Json(new { fileName });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error uploading interview video");
        //        return StatusCode(500, "Error uploading video");
        //    }
        //}

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

        //[HttpGet]
        //public IActionResult ViewInterviewVideo(string fileName)
        //{
        //    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "Uploads", "InterviewVideos", fileName);
        //    if (!System.IO.File.Exists(filePath))
        //    {
        //        return NotFound("Video not found.");
        //    }

        //    var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        //    return File(stream, "video/webm");
        //}

        //[HttpGet]
        //public IActionResult ViewIDProof(string fileName)
        //{
        //    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "Uploads", "IDProofs", fileName);
        //    if (!System.IO.File.Exists(filePath))
        //    {
        //        return NotFound("ID proof not found.");
        //    }

        //    var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        //    return File(stream, "image/jpeg");
        //}

        [HttpPost]
        public async Task<IActionResult> UploadResume(IFormFile resume)
        {
            string userId = HttpContext.Session.Id;
            string response = "";
            string modelUsed = "custom";

            EnsureUserRecord(userId);

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
                    UserId = userId,
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
        public async Task<IActionResult> UploadIDProof()
        {
            var userId = HttpContext.Session.Id;
            var userDetailsStr = HttpContext.Session.GetString("UserDetails");
            var userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : new UserDetails { UserId = userId };
            var chatMessage = new ChatMessage
            {
                UserId = userId,
                UserMessage = "Uploaded ID proof",
                Model = "custom",
                CreatedAt = DateTime.Now
            };

            string response = "";
            EnsureUserRecord(userId);

            try
            {
                var file = Request.Form.Files["idProof"];
                if (file == null || file.Length == 0)
                {
                    response = "No ID proof uploaded. Please upload a JPG, PNG, or PDF of your government-issued ID (e.g., passport, driver's license).";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = response, reason = "no_file", model = "custom", startInterview = false });
                }

                if (file.Length > 5 * 1024 * 1024)
                {
                    response = "File size exceeds 5MB. Please upload a smaller file.";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = response, reason = "file_too_large", model = "custom", startInterview = false });
                }

                var extension = Path.GetExtension(file.FileName).ToLower();
                if (extension != ".jpg" && extension != ".jpeg" && extension != ".png" && extension != ".pdf")
                {
                    response = "Invalid file format. Please upload a JPG, PNG, or PDF file.";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = response, reason = "invalid_format", model = "custom", startInterview = false });
                }

                int retryCount = HttpContext.Session.GetInt32("IDProofRetryCount") ?? 0;
                if (retryCount >= 3)
                {
                    response = "You have exceeded the maximum number of ID proof upload attempts (3). Please contact support or start the application process again.";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    HttpContext.Session.Remove("ApplicationState");
                    HttpContext.Session.Remove("SelectedJob");
                    return Json(new { success = false, message = response, reason = "retry_limit_exceeded", model = "custom", startInterview = false });
                }

                int attemptCount = _chatDbService.GetInterviewAttemptCount(userDetails.Name, userDetails.Email, userDetails.Phone, null);
                if (attemptCount >= 1)
                {
                    response = "❌ You have already attempted the interview for this position. Only one interview attempt is allowed.";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    HttpContext.Session.Remove("ApplicationState");
                    HttpContext.Session.Remove("SelectedJob");
                    return Json(new { success = false, message = response, reason = "interview_limit_exceeded", model = "custom", startInterview = false });
                }

                var userFolderName = string.IsNullOrEmpty(userDetails.Name) ? userId : userDetails.Name.Replace(" ", "_");
                var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), _idProofFolder, userFolderName);
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                var fileName = $"{userFolderName}_{Guid.NewGuid()}{extension}";
                var filePath = Path.Combine(uploadsFolder, fileName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                userDetails.IDProofPath = filePath;
                userDetails.IDProofType = extension == ".pdf" ? "PDF Document" : "Image";
                HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                _chatDbService.SaveUserDetails(userDetails);
                HttpContext.Session.SetInt32("IDProofRetryCount", 0);

                response = $"ID proof uploaded and saved: {fileName}";
                chatMessage.BotResponse = response;
                _chatDbService.SaveMessage(chatMessage);

                var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
                if (!string.IsNullOrEmpty(selectedJob))
                {
                    var nextQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == "AwaitingInterviewStart");
                    if (nextQuestion != null)
                    {
                        HttpContext.Session.SetString("ApplicationState", nextQuestion.State);
                        response = nextQuestion.Prompt;
                        chatMessage.BotResponse = response;
                        _chatDbService.SaveMessage(chatMessage);
                        return Json(new { success = true, response, model = "custom", startInterview = false });
                    }
                    else
                    {
                        response = "❌ Invalid application state after ID upload. Please start the application process again.";
                        chatMessage.BotResponse = response;
                        _chatDbService.SaveMessage(chatMessage);
                        return Json(new { success = false, message = response, reason = "invalid_state", model = "custom", startInterview = false });
                    }
                }
                else
                {
                    response = "❌ No job selected. Please start the application process again or upload your resume to find suitable jobs.";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = response, reason = "no_job_selected", model = "custom", startInterview = false });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading ID proof.");
                response = "Error uploading ID proof. Please try again or contact support.";
                chatMessage.BotResponse = response;
                _chatDbService.SaveMessage(chatMessage);
                return Json(new { success = false, message = response, reason = "exception", model = "error", startInterview = false });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UploadInterviewVideo()
        {
            try
            {
                var file = Request.Form.Files["video"];
                if (file == null || file.Length == 0)
                {
                    return BadRequest("No video file uploaded.");
                }

                var userId = HttpContext.Session.Id;
                var userDetailsStr = HttpContext.Session.GetString("UserDetails");
                var userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : new UserDetails { UserId = userId };

                var userFolderName = string.IsNullOrEmpty(userDetails.Name) ? userId : userDetails.Name.Replace(" ", "_");
                var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), _interviewVideoFolder, userFolderName);
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                var fileName = $"{userFolderName}_{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
                var filePath = Path.Combine(uploadsFolder, fileName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var session = _chatDbService.GetLatestSession(userId);
                if (session != null)
                {
                    session.VideoPath = filePath;
                    _chatDbService.UpdateInterviewSession(session);
                }

                var chatMessage = new ChatMessage
                {
                    UserId = userId,
                    BotResponse = $"Video recorded and saved: {fileName}",
                    CreatedAt = DateTime.Now,
                    Model = "custom"
                };
                _chatDbService.SaveMessage(chatMessage);

                return Json(new { fileName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading interview video");
                return StatusCode(500, "Error uploading video");
            }
        }

        [HttpGet]
        public IActionResult ViewInterviewVideo(string fileName)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), _interviewVideoFolder, fileName);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("Video not found.");
            }

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return File(stream, "video/webm");
        }

        [HttpGet]
        public IActionResult ViewIDProof(string fileName)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), _idProofFolder, fileName);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("ID proof not found.");
            }

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return File(stream, "image/jpeg");
        }
    }
}