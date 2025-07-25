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
using System.Drawing;
using System.Drawing.Imaging;
using OpenCvSharp;
using Tesseract;

namespace ChatBot.Controllers
{
    public class ChatController : Controller
    {
        private readonly ChatGPTService _chatGPTService;
        private readonly ChatDbService _chatDbService;
        private readonly ILogger<ChatController> _logger;
        private readonly List<PreInterviewQuestion> _preInterviewQuestions;
        private readonly int _maxInterviewAttempts; // New: Max interview attempts

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
            _maxInterviewAttempts = configuration.GetSection("InterviewSettings:MaxInterviewAttempts").Get<int>();
        }

        //public class PreInterviewQuestion
        //{
        //    public string State { get; set; }
        //    public string Prompt { get; set; }
        //    public string ValidationRegex { get; set; }
        //    public string ErrorMessage { get; set; }
        //    public bool SkipAllowed { get; set; }
        //    public string SkipToState { get; set; }
        //    public Dictionary<string, string> ConditionalNextStates { get; set; }
        //}

        public class PreInterviewQuestion
        {
            public string State { get; set; }
            public string Prompt { get; set; }
            public string ValidationRegex { get; set; }
            public string ErrorMessage { get; set; }
            public bool SkipAllowed { get; set; }
            public string SkipToState { get; set; }
            public Dictionary<string, string> ConditionalNextStates { get; set; }
            public bool RequiresIDProof { get; set; } // New: Indicates if ID proof is required
            public bool RequiresNameMatch { get; set; } // New: Indicates if name should match ID
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
            bool startInterview = false;

            var message = new ChatMessage
            {
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
                            response = $"✅ Thank you for completing the interview for the position of {session.JobTitle}.\nOur team will review your responses.";
                            userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : null;
                            message.BotResponse = response;
                            sessionMessages.Add(message);
                            _chatDbService.SaveMessage(message);
                            _chatDbService.UpdateInterviewSession(session);
                            SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                            ClearApplicationState();
                            return Json(new { response, model = modelUsed, startInterview = false });
                        }

                        _chatDbService.UpdateInterviewSession(session);
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
                            response = "Please hold your government-issued ID (e.g., passport, driver's license) in front of your face and capture the photo using the webcam capture button below.";
                            message.BotResponse = response;
                            sessionMessages.Add(message);
                            _chatDbService.SaveMessage(message);
                            return Json(new { response, model = modelUsed, startInterview = false });
                        }
                        //else if (appState == "AwaitingInterviewStart")
                        //{
                        //    if (Regex.IsMatch(msg, @"\b(yes|yep|yepp|yeah|sure|start)\b", RegexOptions.IgnoreCase))
                        //    {
                        //        var nextQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == "AwaitingIDProof");
                        //        if (nextQuestion != null)
                        //        {
                        //            HttpContext.Session.SetString("ApplicationState", nextQuestion.State);
                        //            response = nextQuestion.Prompt;
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
                        //        return Json(new { response, model = modelUsed, startInterview = false });
                        //    }
                        //    else
                        //    {
                        //        response = currentQuestion.ErrorMessage;
                        //    }
                        //}

                        else if (appState == "AwaitingInterviewStart")
                        {
                            if (Regex.IsMatch(msg, @"\b(yes|yep|yepp|yeah|sure|start)\b", RegexOptions.IgnoreCase))
                            {
                                // Check interview attempt limit before proceeding
                                int attemptCount = _chatDbService.GetInterviewAttemptCount(userDetails.Name, userDetails.Email, userDetails.Phone, userDetails.DateOfBirth);
                                if (attemptCount >= _maxInterviewAttempts)
                                {
                                    response = $"❌ You have exceeded the maximum number of interview attempts ({_maxInterviewAttempts}).";
                                    message.BotResponse = response;
                                    sessionMessages.Add(message);
                                    _chatDbService.SaveMessage(message);
                                    SaveAndClearSessionMessages(userDetails?.Name, userDetails?.Phone, userDetails?.Email, true);
                                    ClearApplicationState();
                                    return Json(new { response, model = modelUsed, startInterview = false });
                                }

                                var nextQuestion = _preInterviewQuestions.FirstOrDefault(q => q.State == "AwaitingIDProof");
                                if (nextQuestion != null)
                                {
                                    HttpContext.Session.SetString("ApplicationState", nextQuestion.State);
                                    response = nextQuestion.Prompt;
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
                                var nextQuestion = _preInterviewQuestions.SkipWhile(q => q.State != appState).Skip(1).FirstOrDefault();
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
                    }

                    HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
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

        //[HttpPost]
        //public IActionResult UploadIDProof()
        //{
        //    var userId = HttpContext.Session.Id;
        //    var userDetailsStr = HttpContext.Session.GetString("UserDetails");
        //    var userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : new UserDetails { UserId = userId };
        //    var chatMessage = new ChatMessage
        //    {
        //        UserId = userId,
        //        UserMessage = "Attempted to upload ID proof",
        //        Model = "custom",
        //        CreatedAt = DateTime.Now
        //    };

        //    try
        //    {
        //        var file = Request.Form.Files["idProof"];
        //        if (file == null || file.Length == 0)
        //        {
        //            chatMessage.BotResponse = "No ID proof uploaded. Please capture a photo of your government-issued ID with your face visible.";
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = false, message = chatMessage.BotResponse });
        //        }

        //        // Check interview attempt limit
        //        int attemptCount = _chatDbService.GetInterviewAttemptCount(userDetails.Name, userDetails.Email, userDetails.Phone, userDetails.DateOfBirth);
        //        if (attemptCount >= 2)
        //        {
        //            chatMessage.BotResponse = "❌ You have exceeded the maximum number of attempts (2) for this interview.";
        //            _chatDbService.SaveMessage(chatMessage);
        //            HttpContext.Session.Remove("ApplicationState");
        //            HttpContext.Session.Remove("SelectedJob");
        //            return Json(new { success = false, message = chatMessage.BotResponse, model = "custom", startInterview = false });
        //        }

        //        var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Uploads", "IDProofs");
        //        if (!Directory.Exists(uploadsFolder))
        //        {
        //            Directory.CreateDirectory(uploadsFolder);
        //        }

        //        var name = userDetails?.Name?.Replace(" ", "_") ?? userId;
        //        var fileName = $"{name}_{Guid.NewGuid()}.jpg";
        //        var filePath = Path.Combine(uploadsFolder, fileName);

        //        using (var stream = new MemoryStream())
        //        {
        //            file.CopyTo(stream);
        //            stream.Position = 0;

        //            // Check for face and ID document
        //            using (var image = Image.FromStream(stream))
        //            {
        //                bool isBlurry = IsImageBlurry(image);
        //                if (isBlurry)
        //                {
        //                    chatMessage.BotResponse = "The ID proof image is blurry or of poor quality. Please retake the photo with your face and ID clearly visible.";
        //                    _chatDbService.SaveMessage(chatMessage);
        //                    return Json(new { success = false, message = chatMessage.BotResponse });
        //                }

        //                stream.Position = 0;
        //                bool hasFaceAndID = DetectFaceAndID(stream);
        //                if (!hasFaceAndID)
        //                {
        //                    chatMessage.BotResponse = "The image must contain both your face and a government-issued ID (e.g., passport, driver's license). Please retake the photo.";
        //                    _chatDbService.SaveMessage(chatMessage);
        //                    return Json(new { success = false, message = chatMessage.BotResponse });
        //                }

        //                // Extract DOB using OCR
        //                stream.Position = 0;
        //                DateTime? dateOfBirth = ExtractDateOfBirthFromID(stream);
        //                userDetails.DateOfBirth = dateOfBirth;

        //                stream.Position = 0;
        //                using (var fileStream = new FileStream(filePath, FileMode.Create))
        //                {
        //                    stream.CopyTo(fileStream);
        //                }
        //            }
        //        }

        //        // Save ID proof metadata to database
        //        userDetails.IDProofPath = filePath;
        //        HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
        //        _chatDbService.SaveUserDetails(userDetails);

        //        chatMessage.BotResponse = $"ID proof captured and saved: {fileName}";
        //        _chatDbService.SaveMessage(chatMessage);

        //        // Proceed to interview
        //        var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
        //        if (!string.IsNullOrEmpty(selectedJob))
        //        {
        //            var (questions, interviewModel) = _chatGPTService.GenerateRandomInterviewQuestionsWithModelAsync(selectedJob).Result;
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
        //            var response = $"🧪 Starting interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
        //            chatMessage.BotResponse = response;
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = true, response, model = interviewModel, startInterview = true });
        //        }
        //        else
        //        {
        //            chatMessage.BotResponse = "❌ No job selected. Please start the application process again or upload your resume to find suitable jobs.";
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = false, message = chatMessage.BotResponse, model = "custom", startInterview = false });
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error uploading ID proof.");
        //        chatMessage.BotResponse = "Error uploading ID proof. Please try again.";
        //        _chatDbService.SaveMessage(chatMessage);
        //        return Json(new { success = false, message = chatMessage.BotResponse, model = "error" });
        //    }
        //}

        //[HttpPost]
        //public IActionResult UploadIDProof()
        //{
        //    var userId = HttpContext.Session.Id;
        //    var userDetailsStr = HttpContext.Session.GetString("UserDetails");
        //    var userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : new UserDetails { UserId = userId };
        //    var chatMessage = new ChatMessage
        //    {
        //        UserId = userId,
        //        UserMessage = "Attempted to upload ID proof",
        //        Model = "custom",
        //        CreatedAt = DateTime.Now
        //    };

        //    try
        //    {
        //        var file = Request.Form.Files["idProof"];
        //        if (file == null || file.Length == 0)
        //        {
        //            chatMessage.BotResponse = "No ID proof uploaded. Please capture a photo of your government-issued ID with your face visible.";
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = false, message = chatMessage.BotResponse, reason = "no_file" });
        //        }

        //        // Check interview attempt limit
        //        int attemptCount = _chatDbService.GetInterviewAttemptCount(userDetails.Name, userDetails.Email, userDetails.Phone, userDetails.DateOfBirth);
        //        if (attemptCount >= 2)
        //        {
        //            chatMessage.BotResponse = "❌ You have exceeded the maximum number of attempts (2) for this interview.";
        //            _chatDbService.SaveMessage(chatMessage);
        //            HttpContext.Session.Remove("ApplicationState");
        //            HttpContext.Session.Remove("SelectedJob");
        //            return Json(new { success = false, message = chatMessage.BotResponse, reason = "attempt_limit_exceeded", model = "custom", startInterview = false });
        //        }

        //        var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Uploads", "IDProofs");
        //        if (!Directory.Exists(uploadsFolder))
        //        {
        //            Directory.CreateDirectory(uploadsFolder);
        //        }

        //        var name = userDetails?.Name?.Replace(" ", "_") ?? userId;
        //        var fileName = $"{name}_{Guid.NewGuid()}.jpg";
        //        var filePath = Path.Combine(uploadsFolder, fileName);

        //        using (var stream = new MemoryStream())
        //        {
        //            file.CopyTo(stream);
        //            stream.Position = 0;

        //            // Check for face and ID document
        //            using (var image = Image.FromStream(stream))
        //            {
        //                bool isBlurry = IsImageBlurry(image);
        //                if (isBlurry)
        //                {
        //                    chatMessage.BotResponse = "The ID proof image is blurry or of poor quality. Please retake the photo with your face and ID clearly visible.";
        //                    _chatDbService.SaveMessage(chatMessage);
        //                    return Json(new { success = false, message = chatMessage.BotResponse, reason = "blurry_image" });
        //                }

        //                stream.Position = 0;
        //                bool hasFaceAndID = DetectFaceAndID(stream);
        //                if (!hasFaceAndID)
        //                {
        //                    chatMessage.BotResponse = "The image must contain both your face and a government-issued ID (e.g., passport, driver's license). Please retake the photo.";
        //                    _chatDbService.SaveMessage(chatMessage);
        //                    return Json(new { success = false, message = chatMessage.BotResponse, reason = "no_face_or_id" });
        //                }

        //                stream.Position = 0;
        //                DateTime? dateOfBirth = ExtractDateOfBirthFromID(stream);
        //                userDetails.DateOfBirth = dateOfBirth;

        //                stream.Position = 0;
        //                using (var fileStream = new FileStream(filePath, FileMode.Create))
        //                {
        //                    stream.CopyTo(fileStream);
        //                }
        //            }
        //        }

        //        userDetails.IDProofPath = filePath;
        //        HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
        //        _chatDbService.SaveUserDetails(userDetails);

        //        chatMessage.BotResponse = $"ID proof captured and saved: {fileName}";
        //        _chatDbService.SaveMessage(chatMessage);

        //        var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
        //        if (!string.IsNullOrEmpty(selectedJob))
        //        {
        //            var (questions, interviewModel) = _chatGPTService.GenerateRandomInterviewQuestionsWithModelAsync(selectedJob).Result;
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
        //            var response = $"🧪 Starting interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
        //            chatMessage.BotResponse = response;
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = true, response, model = interviewModel, startInterview = true });
        //        }
        //        else
        //        {
        //            chatMessage.BotResponse = "❌ No job selected. Please start the application process again or upload your resume to find suitable jobs.";
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = false, message = chatMessage.BotResponse, reason = "no_job_selected", model = "custom", startInterview = false });
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error uploading ID proof.");
        //        chatMessage.BotResponse = "Error uploading ID proof. Please try again.";
        //        _chatDbService.SaveMessage(chatMessage);
        //        return Json(new { success = false, message = chatMessage.BotResponse, reason = "exception", model = "error" });
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
        //        UserMessage = "Attempted to upload ID proof",
        //        Model = "custom",
        //        CreatedAt = DateTime.Now
        //    };

        //    try
        //    {
        //        var file = Request.Form.Files["idProof"];
        //        if (file == null || file.Length == 0)
        //        {
        //            chatMessage.BotResponse = "No ID proof uploaded. Please capture a photo of your government-issued ID with your face visible.";
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = false, message = chatMessage.BotResponse, reason = "no_file" });
        //        }

        //        // Check interview attempt limit
        //        int attemptCount = _chatDbService.GetInterviewAttemptCount(userDetails.Name, userDetails.Email, userDetails.Phone, userDetails.DateOfBirth);
        //        if (attemptCount >= 2)
        //        {
        //            chatMessage.BotResponse = "❌ You have exceeded the maximum number of attempts (2) for this interview.";
        //            _chatDbService.SaveMessage(chatMessage);
        //            HttpContext.Session.Remove("ApplicationState");
        //            HttpContext.Session.Remove("SelectedJob");
        //            return Json(new { success = false, message = chatMessage.BotResponse, reason = "attempt_limit_exceeded", model = "custom", startInterview = false });
        //        }

        //        var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Uploads", "IDProofs");
        //        if (!Directory.Exists(uploadsFolder))
        //        {
        //            Directory.CreateDirectory(uploadsFolder);
        //        }

        //        var name = userDetails?.Name?.Replace(" ", "_") ?? userId;
        //        var fileName = $"{name}_{Guid.NewGuid()}.jpg";
        //        var filePath = Path.Combine(uploadsFolder, fileName);

        //        using (var stream = new MemoryStream())
        //        {
        //            await file.CopyToAsync(stream); // Use async file copy
        //            stream.Position = 0;

        //            // Check for face and ID document
        //            using (var image = Image.FromStream(stream))
        //            {
        //                bool isBlurry = IsImageBlurry(image);
        //                if (isBlurry)
        //                {
        //                    chatMessage.BotResponse = "The ID proof image is blurry or of poor quality. Please retake the photo with your face and ID clearly visible.";
        //                    _chatDbService.SaveMessage(chatMessage);
        //                    return Json(new { success = false, message = chatMessage.BotResponse, reason = "blurry_image" });
        //                }

        //                stream.Position = 0;
        //                bool hasFaceAndID = await DetectFaceAndID(stream); // Use await for async method
        //                if (!hasFaceAndID)
        //                {
        //                    chatMessage.BotResponse = "The image must contain both your face and a government-issued ID (e.g., passport, driver's license). Please retake the photo.";
        //                    _chatDbService.SaveMessage(chatMessage);
        //                    return Json(new { success = false, message = chatMessage.BotResponse, reason = "no_face_or_id" });
        //                }

        //                stream.Position = 0;
        //                DateTime? dateOfBirth = ExtractDateOfBirthFromID(stream); // Ensure ExtractDateOfBirthFromID is also updated if async
        //                userDetails.DateOfBirth = dateOfBirth;

        //                stream.Position = 0;
        //                using (var fileStream = new FileStream(filePath, FileMode.Create))
        //                {
        //                    await stream.CopyToAsync(fileStream); // Use async file copy
        //                }
        //            }
        //        }

        //        userDetails.IDProofPath = filePath;
        //        HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
        //        _chatDbService.SaveUserDetails(userDetails);

        //        chatMessage.BotResponse = $"ID proof captured and saved: {fileName}";
        //        _chatDbService.SaveMessage(chatMessage);

        //        var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
        //        if (!string.IsNullOrEmpty(selectedJob))
        //        {
        //            var (questions, interviewModel) = await _chatGPTService.GenerateRandomInterviewQuestionsWithModelAsync(selectedJob); // Use await instead of .Result
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
        //            var response = $"🧪 Starting interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
        //            chatMessage.BotResponse = response;
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = true, response, model = interviewModel, startInterview = true });
        //        }
        //        else
        //        {
        //            chatMessage.BotResponse = "❌ No job selected. Please start the application process again or upload your resume to find suitable jobs.";
        //            _chatDbService.SaveMessage(chatMessage);
        //            return Json(new { success = false, message = chatMessage.BotResponse, reason = "no_job_selected", model = "custom", startInterview = false });
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error uploading ID proof.");
        //        chatMessage.BotResponse = "Error uploading ID proof. Please try again.";
        //        _chatDbService.SaveMessage(chatMessage);
        //        return Json(new { success = false, message = chatMessage.BotResponse, reason = "exception", model = "error" });
        //    }
        //}

        [HttpPost]
        public async Task<IActionResult> UploadIDProof()
        {
            var userId = HttpContext.Session.Id;
            var userDetailsStr = HttpContext.Session.GetString("UserDetails");
            var userDetails = userDetailsStr is not null ? JsonConvert.DeserializeObject<UserDetails>(userDetailsStr) : new UserDetails { UserId = userId };
            var chatMessage = new ChatMessage
            {
                UserId = userId,
                UserMessage = "Attempted to upload ID proof",
                Model = "custom",
                CreatedAt = DateTime.Now
            };

            try
            {
                var file = Request.Form.Files["idProof"];
                if (file == null || file.Length == 0)
                {
                    chatMessage.BotResponse = "No ID proof uploaded. Please upload a clear photo or PDF of your government-issued ID.";
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = chatMessage.BotResponse, reason = "no_file" });
                }

                // Check file size (5MB limit)
                if (file.Length > 5 * 1024 * 1024)
                {
                    chatMessage.BotResponse = "File size exceeds 5MB. Please upload a smaller file.";
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = chatMessage.BotResponse, reason = "file_too_large" });
                }

                // Validate file extension
                var extension = Path.GetExtension(file.FileName).ToLower();
                if (extension != ".jpg" && extension != ".jpeg" && extension != ".png" && extension != ".pdf")
                {
                    chatMessage.BotResponse = "Invalid file format. Please upload a JPG, PNG, or PDF file.";
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = chatMessage.BotResponse, reason = "invalid_format" });
                }

                // Check interview attempt limit
                int attemptCount = _chatDbService.GetInterviewAttemptCount(userDetails.Name, userDetails.Email, userDetails.Phone, userDetails.DateOfBirth);
                if (attemptCount >= _maxInterviewAttempts)
                {
                    chatMessage.BotResponse = $"❌ You have exceeded the maximum number of attempts ({_maxInterviewAttempts}) for this interview.";
                    _chatDbService.SaveMessage(chatMessage);
                    HttpContext.Session.Remove("ApplicationState");
                    HttpContext.Session.Remove("SelectedJob");
                    return Json(new { success = false, message = chatMessage.BotResponse, reason = "attempt_limit_exceeded", model = "custom", startInterview = false });
                }

                // Create user-specific folder
                var userFolderName = string.IsNullOrEmpty(userDetails.Name) ? userId : userDetails.Name.Replace(" ", "_");
                var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Uploads", "IDProofs", userFolderName);
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                var fileName = $"{userFolderName}_{Guid.NewGuid()}{extension}";
                var filePath = Path.Combine(uploadsFolder, fileName);

                using (var stream = new MemoryStream())
                {
                    await file.CopyToAsync(stream);
                    stream.Position = 0;

                    string extractedText = "";
                    string idProofType = "";
                    string extractedName = "";
                    DateTime? dateOfBirth = null;
                    bool isValid = false;

                    if (extension == ".pdf")
                    {
                        // Extract text from PDF
                        using (var reader = new PdfReader(stream))
                        using (var pdfDoc = new PdfDocument(reader))
                        {
                            var text = new StringBuilder();
                            for (int i = 1; i <= pdfDoc.GetNumberOfPages(); i++)
                            {
                                text.Append(PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(i)));
                            }
                            extractedText = text.ToString();
                        }

                        // Assume PDF is clear (no blur check for PDFs)
                        isValid = !string.IsNullOrWhiteSpace(extractedText);
                    }
                    else
                    {
                        // Check for blur in images
                        using (var image = Image.FromStream(stream))
                        {
                            bool isBlurry = IsImageBlurry(image);
                            if (isBlurry)
                            {
                                chatMessage.BotResponse = "The ID proof image is blurry or of poor quality. Please upload a clearer photo.";
                                _chatDbService.SaveMessage(chatMessage);
                                return Json(new { success = false, message = chatMessage.BotResponse, reason = "blurry_image" });
                            }
                        }

                        // Extract text from image using OCR
                        stream.Position = 0;
                        (extractedText, dateOfBirth) = ExtractTextAndDOBFromImage(stream);
                        isValid = !string.IsNullOrWhiteSpace(extractedText);
                    }

                    if (!isValid)
                    {
                        chatMessage.BotResponse = "Unable to extract content from the ID proof. Please upload a clear photo or PDF.";
                        _chatDbService.SaveMessage(chatMessage);
                        return Json(new { success = false, message = chatMessage.BotResponse, reason = "no_content" });
                    }

                    // Identify ID proof type
                    extractedText = extractedText.ToLower();
                    if (extractedText.Contains("passport"))
                        idProofType = "Passport";
                    else if (extractedText.Contains("driver") || extractedText.Contains("license") || extractedText.Contains("licence"))
                        idProofType = "Driver's License";
                    else if (extractedText.Contains("id card") || extractedText.Contains("identification"))
                        idProofType = "ID Card";
                    else
                    {
                        chatMessage.BotResponse = "Could not identify the ID proof type. Please upload a government-issued ID (e.g., passport, driver's license).";
                        _chatDbService.SaveMessage(chatMessage);
                        return Json(new { success = false, message = chatMessage.BotResponse, reason = "unknown_id_type" });
                    }

                    // Extract name from text
                    extractedName = ExtractNameFromText(extractedText);
                    if (string.IsNullOrEmpty(extractedName))
                    {
                        chatMessage.BotResponse = "Could not extract a name from the ID proof. Please ensure the ID contains a readable name.";
                        _chatDbService.SaveMessage(chatMessage);
                        return Json(new { success = false, message = chatMessage.BotResponse, reason = "no_name" });
                    }

                    // Verify name matches provided name
                    if (!_chatDbService.VerifyIDProof(userId, extractedName, userDetails.Name))
                    {
                        chatMessage.BotResponse = "The name on the ID proof does not match the provided name. Please upload an ID proof that matches your provided name.";
                        _chatDbService.SaveMessage(chatMessage);
                        return Json(new { success = false, message = chatMessage.BotResponse, reason = "name_mismatch" });
                    }

                    // Save file
                    stream.Position = 0;
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await stream.CopyToAsync(fileStream);
                    }

                    // Update user details
                    userDetails.IDProofPath = filePath;
                    userDetails.IDProofType = idProofType;
                    userDetails.ExtractedName = extractedName;
                    userDetails.DateOfBirth = dateOfBirth;
                    HttpContext.Session.SetString("UserDetails", JsonConvert.SerializeObject(userDetails));
                    _chatDbService.SaveUserDetails(userDetails);
                }

                //chatMessage.BotResponse = $"ID proof ({idProofType}) captured and saved: {fileName}";
                _chatDbService.SaveMessage(chatMessage);

                // Proceed to interview
                var selectedJob = HttpContext.Session.GetString("SelectedJob") ?? "";
                if (!string.IsNullOrEmpty(selectedJob))
                {
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
                    var response = $"🧪 Starting interview for {selectedJob}.\n❓ Question 1: {questions[0]}";
                    chatMessage.BotResponse = response;
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = true, response, model = interviewModel, startInterview = true });
                }
                else
                {
                    chatMessage.BotResponse = "❌ No job selected. Please start the application process again or upload your resume to find suitable jobs.";
                    _chatDbService.SaveMessage(chatMessage);
                    return Json(new { success = false, message = chatMessage.BotResponse, reason = "no_job_selected", model = "custom", startInterview = false });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading ID proof.");
                chatMessage.BotResponse = "Error uploading ID proof. Please try again.";
                _chatDbService.SaveMessage(chatMessage);
                return Json(new { success = false, message = chatMessage.BotResponse, reason = "exception", model = "error" });
            }
        }

        private (string Text, DateTime? DOB) ExtractTextAndDOBFromImage(Stream imageStream)
        {
            try
            {
                imageStream.Position = 0;
                byte[] imageBytes;
                using (var ms = new MemoryStream())
                {
                    imageStream.CopyTo(ms);
                    imageBytes = ms.ToArray();
                }

                using var engine = new TesseractEngine(@"./tessdata", "eng", EngineMode.Default);
                using var image = Pix.LoadFromMemory(imageBytes);
                using var page = engine.Process(image);
                string text = page.GetText();

                // Extract DOB
                var dateRegex = new Regex(@"\b(\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}[/-]\d{1,2}[/-]\d{1,2})\b");
                var match = dateRegex.Match(text);
                DateTime? dob = match.Success && DateTime.TryParse(match.Groups[1].Value, out var parsedDob) ? parsedDob : null;

                return (text, dob);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting text and DOB from image.");
                return ("", null);
            }
        }

        private string ExtractNameFromText(string text)
        {
            try
            {
                // Simple regex to find potential names (two or more words with letters)
                var nameRegex = new Regex(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b");
                var match = nameRegex.Match(text);
                return match.Success ? match.Groups[1].Value : "";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting name from text.");
                return "";
            }
        }

        private bool IsImageBlurry(Image image)
        {
            using (var bitmap = new Bitmap(image))
            {
                var grayscale = new Bitmap(bitmap.Width, bitmap.Height);
                for (int y = 0; y < bitmap.Height; y++)
                {
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        int gray = (int)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
                        grayscale.SetPixel(x, y, Color.FromArgb(gray, gray, gray));
                    }
                }

                double[,] laplacian = new double[,]
                {
                    { 0,  1,  0 },
                    { 1, -4,  1 },
                    { 0,  1,  0 }
                };

                double sum = 0;
                int count = 0;
                for (int y = 1; y < grayscale.Height - 1; y++)
                {
                    for (int x = 1; x < grayscale.Width - 1; x++)
                    {
                        double laplacianValue = 0;
                        for (int ky = -1; ky <= 1; ky++)
                        {
                            for (int kx = -1; kx <= 1; kx++)
                            {
                                var pixel = grayscale.GetPixel(x + kx, y + ky);
                                laplacianValue += pixel.R * laplacian[ky + 1, kx + 1];
                            }
                        }
                        sum += laplacianValue * laplacianValue;
                        count++;
                    }
                }

                double variance = sum / count;
                return variance < 100;
            }
        }

        //private bool DetectFaceAndID(Stream imageStream)
        //{
        //    try
        //    {
        //        imageStream.Position = 0;
        //        byte[] imageBytes;
        //        using (var ms = new MemoryStream())
        //        {
        //            imageStream.CopyTo(ms);
        //            imageBytes = ms.ToArray();
        //        }

        //        using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        //        if (mat.Empty())
        //        {
        //            _logger.LogWarning("Failed to decode image for face and ID detection.");
        //            return false;
        //        }

        //        // Load Haar cascade for face detection
        //        using var faceCascade = new CascadeClassifier("haarcascade_frontalface_default.xml");

        //        // Detect faces
        //        var faces = faceCascade.DetectMultiScale(mat, scaleFactor: 1.1, minNeighbors: 5, minSize: new OpenCvSharp.Size(30, 30));
        //        if (faces.Length == 0)
        //        {
        //            _logger.LogWarning("No faces detected in the image.");
        //            return false;
        //        }

        //        // Use Tesseract for ID detection
        //        using var engine = new TesseractEngine(@"./tessdata", "eng", EngineMode.Default);
        //        using var image = Pix.LoadFromMemory(imageBytes);
        //        using var page = engine.Process(image);
        //        string text = page.GetText().ToLower();
        //        bool idDetected = text.Contains("passport") || text.Contains("driver") || text.Contains("license") || text.Contains("id card");
        //        if (!idDetected)
        //        {
        //            _logger.LogWarning("No ID card detected in the image.");
        //            return false;
        //        }

        //        return true;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error detecting face and ID in image.");
        //        return false;
        //    }
        //}

        //private bool DetectFaceAndID(Stream imageStream)
        //{
        //    try
        //    {
        //        // Verify file existence
        //        string haarCascadePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "haarcascade_frontalface_default.xml");
        //        if (!System.IO.File.Exists(haarCascadePath))
        //        {
        //            _logger.LogError($"Haar cascade file not found at: {haarCascadePath}");
        //            return false;
        //        }

        //        string tessdataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
        //        if (!Directory.Exists(tessdataPath) || !System.IO.File.Exists(Path.Combine(tessdataPath, "eng.traineddata")))
        //        {
        //            _logger.LogError($"Tesseract data folder or 'eng.traineddata' not found at: {tessdataPath}");
        //            return false;
        //        }

        //        imageStream.Position = 0;
        //        byte[] imageBytes;
        //        using (var ms = new MemoryStream())
        //        {
        //            imageStream.CopyTo(ms);
        //            imageBytes = ms.ToArray();
        //        }

        //        // Log image size for debugging
        //        _logger.LogInformation($"Image size: {imageBytes.Length} bytes");

        //        using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        //        if (mat.Empty())
        //        {
        //            _logger.LogWarning("Failed to decode image for face and ID detection.");
        //            return false;
        //        }

        //        // Convert to grayscale for face detection
        //        using var gray = new Mat();
        //        Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
        //        Cv2.EqualizeHist(gray, gray); // Improve contrast

        //        // Load Haar cascade for face detection
        //        using var faceCascade = new CascadeClassifier(haarCascadePath);

        //        // Detect faces with slightly more lenient parameters
        //        var faces = faceCascade.DetectMultiScale(
        //            image: gray,
        //            scaleFactor: 1.05, // More sensitive scaling
        //            minNeighbors: 3,   // Less strict neighbor requirement
        //            flags: HaarDetectionTypes.ScaleImage,
        //            minSize: new OpenCvSharp.Size(20, 20) // Smaller minimum face size
        //        );
        //        _logger.LogInformation($"Detected {faces.Length} faces in the image.");
        //        if (faces.Length == 0)
        //        {
        //            _logger.LogWarning("No faces detected in the image.");
        //            return false;
        //        }

        //        // Preprocess image for Tesseract
        //        using var tesseractMat = new Mat();
        //        Cv2.CvtColor(mat, tesseractMat, ColorConversionCodes.BGR2GRAY);
        //        Cv2.Threshold(tesseractMat, tesseractMat, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        //        // Resize to increase DPI for better OCR (target ~300 DPI)
        //        double scaleFactor = 300.0 / 72.0; // Assuming input is ~72 DPI
        //        Cv2.Resize(tesseractMat, tesseractMat, new OpenCvSharp.Size(0, 0), scaleFactor, scaleFactor, InterpolationFlags.Linear);

        //        // Save preprocessed image for debugging
        //        var tempPath = Path.Combine(Path.GetTempPath(), $"preprocessed_id_{Guid.NewGuid()}.jpg");
        //        Cv2.ImWrite(tempPath, tesseractMat);
        //        _logger.LogInformation($"Preprocessed image saved for debugging at: {tempPath}");

        //        // Use Tesseract for ID detection
        //        using var engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default);
        //        using var image = Pix.LoadFromMemory(imageBytes);
        //        using var page = engine.Process(image);
        //        string text = page.GetText()?.ToLower() ?? "";
        //        _logger.LogInformation($"Tesseract extracted text: {text}");

        //        // Expanded keyword list for ID detection
        //        bool idDetected = text.Contains("passport") ||
        //                          text.Contains("driver") ||
        //                          text.Contains("license") ||
        //                          text.Contains("licence") ||
        //                          text.Contains("id card") ||
        //                          text.Contains("identification") ||
        //                          text.Contains("government") ||
        //                          text.Contains("card") ||
        //                          text.Contains("permit") ||
        //                          text.Contains("identity");
        //        if (!idDetected)
        //        {
        //            _logger.LogWarning("No ID card detected in the image. Keywords not found.");
        //            return false;
        //        }

        //        return true;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error detecting face and ID in image.");
        //        return false;
        //    }
        //}


        private async Task<bool> DetectFaceAndID(Stream imageStream)
        {
            byte[] imageBytes = null;
            Mat mat = null;
            try
            {
                // Verify file existence
                string haarCascadePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "haarcascade_frontalface_default.xml");
                if (!System.IO.File.Exists(haarCascadePath))
                {
                    _logger.LogError($"Haar cascade file not found at: {haarCascadePath}");
                    return false;
                }

                string tessdataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
                if (!Directory.Exists(tessdataPath) || !System.IO.File.Exists(Path.Combine(tessdataPath, "eng.traineddata")))
                {
                    _logger.LogError($"Tesseract data folder or 'eng.traineddata' not found at: {tessdataPath}");
                    return false;
                }

                imageStream.Position = 0;
                using (var ms = new MemoryStream())
                {
                    await imageStream.CopyToAsync(ms);
                    imageBytes = ms.ToArray();
                }

                _logger.LogInformation($"Image size: {imageBytes.Length} bytes");

                // Save original image for debugging
                var originalImagePath = Path.Combine(Path.GetTempPath(), $"original_id_{Guid.NewGuid()}.jpg");
                try
                {
                    await System.IO.File.WriteAllBytesAsync(originalImagePath, imageBytes);
                    _logger.LogInformation($"Original image saved for debugging at: {originalImagePath}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to save original image at: {originalImagePath}");
                }

                mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
                if (mat.Empty())
                {
                    _logger.LogWarning("Failed to decode image for face and ID detection.");
                    return false;
                }

                _logger.LogInformation($"Image dimensions: {mat.Width}x{mat.Height}, Channels: {mat.Channels()}");
                if (mat.Width > 2000 || mat.Height > 2000)
                {
                    Cv2.Resize(mat, mat, new OpenCvSharp.Size(mat.Width / 2, mat.Height / 2), 0, 0, InterpolationFlags.Linear);
                    _logger.LogInformation($"Resized image to: {mat.Width}x{mat.Height}");
                }

                // Face detection
                using var gray = new Mat();
                Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.EqualizeHist(gray, gray);
                using var faceCascade = new CascadeClassifier(haarCascadePath);
                var faces = faceCascade.DetectMultiScale(
                    image: gray,
                    scaleFactor: 1.05,
                    minNeighbors: 3,
                    flags: HaarDetectionTypes.ScaleImage,
                    minSize: new OpenCvSharp.Size(20, 20)
                );
                _logger.LogInformation($"Detected {faces.Length} faces in the image.");
                if (faces.Length == 0)
                {
                    _logger.LogWarning("No faces detected in the image.");
                    return false;
                }

                // Preprocess for Tesseract
                using var tesseractMat = new Mat();
                Cv2.CvtColor(mat, tesseractMat, ColorConversionCodes.BGR2GRAY);
                // Try multiple preprocessing approaches
                using var binaryMat = new Mat();
                Cv2.AdaptiveThreshold(tesseractMat, binaryMat, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 11, 2);
                Cv2.GaussianBlur(binaryMat, binaryMat, new OpenCvSharp.Size(3, 3), 0);
                double scaleFactor = 300.0 / 72.0;
                Cv2.Resize(binaryMat, binaryMat, new OpenCvSharp.Size(0, 0), scaleFactor, scaleFactor, InterpolationFlags.Linear);

                // Save preprocessed image for debugging
                var tempPath = Path.Combine(Path.GetTempPath(), $"preprocessed_id_{Guid.NewGuid()}.jpg");
                try
                {
                    Cv2.ImWrite(tempPath, binaryMat);
                    _logger.LogInformation($"Preprocessed image saved for debugging at: {tempPath}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to save preprocessed image at: {tempPath}");
                }

                // Tesseract OCR with multiple page segmentation modes
                string text = "";
                var pageSegModes = new[] { PageSegMode.AutoOsd, PageSegMode.SingleBlock, PageSegMode.SparseText };
                bool osdAvailable = System.IO.File.Exists(Path.Combine(tessdataPath, "osd.traineddata"));

                foreach (var mode in pageSegModes)
                {
                    if (mode == PageSegMode.AutoOsd && !osdAvailable)
                    {
                        _logger.LogInformation("Skipping PageSegMode.AutoOsd as osd.traineddata is not available.");
                        continue;
                    }

                    try
                    {
                        using var engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default);
                        engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-/ ");
                        engine.SetVariable("preserve_interword_spaces", "1");
                        var ocrTask = Task.Run(() =>
                        {
                            using var image = Pix.LoadFromMemory(imageBytes);
                            using var page = engine.Process(image, mode);
                            return page.GetText()?.ToLower() ?? "";
                        });
                        if (await Task.WhenAny(ocrTask, Task.Delay(TimeSpan.FromSeconds(10))) != ocrTask)
                        {
                            _logger.LogWarning($"Tesseract OCR timed out after 10 seconds for PageSegMode: {mode}.");
                            continue;
                        }
                        text = await ocrTask;
                        _logger.LogInformation($"Tesseract extracted text with PageSegMode {mode}: {text.Replace("\n", "\\n")}");

                        if (!string.IsNullOrWhiteSpace(text) && text.Length > 5) // Ensure sufficient text
                        {
                            break; // Exit loop if we get meaningful text
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Tesseract OCR failed for PageSegMode: {mode}.");
                    }
                }

                if (string.IsNullOrWhiteSpace(text) || text.Length <= 5)
                {
                    _logger.LogWarning("No meaningful text extracted from Tesseract after trying multiple page segmentation modes.");
                    return false;
                }

                // Expanded keyword detection
                bool idDetected = text.Contains("passport") ||
                                  text.Contains("driver") ||
                                  text.Contains("license") ||
                                  text.Contains("licence") ||
                                  text.Contains("id card") ||
                                  text.Contains("identification") ||
                                  text.Contains("government") ||
                                  text.Contains("card") ||
                                  text.Contains("permit") ||
                                  text.Contains("identity") ||
                                  Regex.IsMatch(text, @"\b\d{2,4}[-/]\d{1,2}[-/]\d{2,4}\b");
                if (!idDetected)
                {
                    _logger.LogWarning($"No ID card detected in the image. Keywords not found. Extracted text: {text.Replace("\n", "\\n")}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting face and ID in image. Image size: {ImageSize}, Dimensions: {Width}x{Height}",
                    imageBytes?.Length ?? 0, mat?.Width ?? 0, mat?.Height ?? 0);
                return false;
            }
            finally
            {
                mat?.Dispose();
            }
        }
        private DateTime? ExtractDateOfBirthFromID(Stream imageStream)
        {
            try
            {
                imageStream.Position = 0;
                byte[] imageBytes;
                using (var ms = new MemoryStream())
                {
                    imageStream.CopyTo(ms);
                    imageBytes = ms.ToArray();
                }

                using var engine = new TesseractEngine(@"./tessdata", "eng", EngineMode.Default);
                using var image = Pix.LoadFromMemory(imageBytes);
                using var page = engine.Process(image);
                string text = page.GetText();

                // Regex for common date formats (e.g., MM/DD/YYYY, DD-MM-YYYY, YYYY/MM/DD)
                var dateRegex = new Regex(@"\b(\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}[/-]\d{1,2}[/-]\d{1,2})\b");
                var match = dateRegex.Match(text);
                if (match.Success && DateTime.TryParse(match.Groups[1].Value, out var dob))
                {
                    return dob;
                }

                _logger.LogWarning("No valid date of birth found in ID proof.");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting DOB from ID proof.");
                return null;
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
        public IActionResult UploadInterviewVideo()
        {
            try
            {
                var file = Request.Form.Files["video"];
                if (file == null || file.Length == 0)
                {
                    return BadRequest("No video file uploaded.");
                }

                var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Uploads", "InterviewVideos");
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
                var filePath = Path.Combine(uploadsFolder, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    file.CopyTo(stream);
                }

                var userId = HttpContext.Session.Id;
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
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Uploads", "InterviewVideos", fileName);
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
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Uploads", "IDProofs", fileName);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("ID proof not found.");
            }

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return File(stream, "image/jpeg");
        }
    }
}