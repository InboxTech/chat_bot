using ChatBot.Models;
using ChatBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static ChatBot.Controllers.ChatController;

namespace ChatBot.Controllers
{
    public class AdminController : Controller
    {
        private readonly string _connectionString;
        private readonly ILogger<AdminController> _logger;
        private readonly NotificationService _notificationService;
        private readonly ChatDbService _chatDbService;
        private readonly IConfiguration _configuration;
        private readonly List<PreInterviewQuestion> _preInterviewQuestions;
        private readonly string _resumeFolder;
        private readonly string _idProofFolder;
        private readonly string _interviewVideoFolder;

        public AdminController(IConfiguration configuration, ILogger<AdminController> logger, NotificationService notificationService, ChatDbService chatDbService)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _logger = logger;
            _notificationService = notificationService;
            _chatDbService = chatDbService;
            _configuration = configuration;
            _preInterviewQuestions = configuration.GetSection("PreInterviewQuestions").Get<List<PreInterviewQuestion>>();
            _resumeFolder = configuration.GetSection("UploadPaths:ResumeFolder").Value;
            _idProofFolder = configuration.GetSection("UploadPaths:IDProofFolder").Value;
            _interviewVideoFolder = configuration.GetSection("UploadPaths:InterviewVideoFolder").Value;
        }

        [HttpGet]
        public IActionResult Index()
        {
            var userDetailsList = new List<UserAdminViewModel>();
            try
            {
                if (string.IsNullOrEmpty(_connectionString))
                {
                    var errorMsg = "Connection string 'DefaultConnection' is missing or empty.";
                    _logger.LogError(errorMsg);
                    ViewBag.ErrorMessage = errorMsg;
                    return View(userDetailsList);
                }

                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                _logger.LogInformation("Successfully connected to the database.");

                var cmd = new SqlCommand(@"
                    SELECT 
                        u.UserId, 
                        u.Name, 
                        u.Phone, 
                        u.Email, 
                        u.CreatedAt, 
                        u.IDProofPath, 
                        u.IDProofType, 
                        u.EmailSent, 
                        u.WhatsAppSent, 
                        i.VideoPath,
                        ISNULL((
                            SELECT COUNT(*) 
                            FROM Interactions 
                            WHERE UserId = u.UserId 
                            AND IsComplete = 1
                            AND Questions IS NOT NULL
                        ), 0) AS InterviewCount,
                        ISNULL((
                            SELECT TOP 1 CASE 
                                WHEN IsComplete = 1 AND IsSubmitted = 1 THEN 'Submitted'
                                WHEN IsComplete = 1 AND IsSubmitted = 0 THEN 'Completed but not submitted'
                                WHEN IsComplete = 0 THEN 'In progress'
                                ELSE 'Not started'
                            END 
                            FROM Interactions 
                            WHERE UserId = u.UserId 
                            AND Questions IS NOT NULL
                            ORDER BY CreatedAt DESC
                        ), 'Not started') AS InterviewStatus
                    FROM Users u
                    LEFT JOIN Interactions i ON u.UserId = i.UserId 
                        AND i.InteractionId = (
                            SELECT MAX(InteractionId) 
                            FROM Interactions 
                            WHERE UserId = u.UserId 
                            AND IsComplete = 1
                            AND Questions IS NOT NULL
                        )", conn);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var user = new UserAdminViewModel
                    {
                        UserId = reader["UserId"].ToString(),
                        Name = reader["Name"] != DBNull.Value ? reader["Name"].ToString() : string.Empty,
                        Phone = reader["Phone"] != DBNull.Value ? reader["Phone"].ToString() : string.Empty,
                        Email = reader["Email"] != DBNull.Value ? reader["Email"].ToString() : string.Empty,
                        CreatedAt = reader["CreatedAt"] != DBNull.Value ? (DateTime?)reader["CreatedAt"] : null,
                        IDProofPath = reader["IDProofPath"] != DBNull.Value ? reader["IDProofPath"].ToString() : string.Empty,
                        IDProofType = reader["IDProofType"] != DBNull.Value ? reader["IDProofType"].ToString() : string.Empty,
                        InterviewVideoPath = reader["VideoPath"] != DBNull.Value ? reader["VideoPath"].ToString() : string.Empty,
                        InterviewCount = reader["InterviewCount"] != DBNull.Value ? (int)reader["InterviewCount"] : 0,
                        InterviewStatus = reader["InterviewStatus"] != DBNull.Value ? reader["InterviewStatus"].ToString() : "Not started",
                        EmailSent = reader["EmailSent"] != DBNull.Value ? (bool)reader["EmailSent"] : false,
                        WhatsAppSent = reader["WhatsAppSent"] != DBNull.Value ? (bool)reader["WhatsAppSent"] : false
                    };
                    userDetailsList.Add(user);
                }
                reader.Close();

                try
                {
                    ViewData["Templates"] = _chatDbService.GetMessageTemplates() ?? new List<MessageTemplate>();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error retrieving message templates");
                    ViewData["Templates"] = new List<MessageTemplate>();
                }

                return View(userDetailsList);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving user data for admin dashboard");
                ViewBag.ErrorMessage = "An unexpected error occurred. Please check the server logs or try again later.";
                return View(userDetailsList);
            }
        }

        [HttpGet]
        public IActionResult ViewIDProof(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                _logger.LogWarning("ViewIDProof called with null or empty filePath");
                return NotFound("ID proof not found.");
            }

            var fullPath = filePath.Contains(_idProofFolder) ? filePath : Path.Combine(Directory.GetCurrentDirectory(), _idProofFolder, filePath);
            if (!System.IO.File.Exists(fullPath))
            {
                _logger.LogWarning("ID proof not found at path: {FilePath}", fullPath);
                return NotFound("ID proof not found.");
            }

            var extension = Path.GetExtension(fullPath).ToLower();
            string contentType = extension switch
            {
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".pdf" => "application/pdf",
                _ => "application/octet-stream"
            };

            try
            {
                _logger.LogInformation("Serving ID proof from: {FilePath}", fullPath);
                var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
                return File(stream, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accessing ID proof file at path: {FilePath}", fullPath);
                return NotFound("Error accessing ID proof file.");
            }
        }

        [HttpGet]
        public IActionResult ViewInterviewVideo(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                _logger.LogWarning("ViewInterviewVideo called with null or empty filePath");
                return NotFound("Video not found.");
            }

            var fullPath = filePath.Contains(_interviewVideoFolder) ? filePath : Path.Combine(Directory.GetCurrentDirectory(), _interviewVideoFolder, filePath);
            if (!System.IO.File.Exists(fullPath))
            {
                _logger.LogWarning("Interview video not found at path: {FilePath}", fullPath);
                return NotFound("Video not found.");
            }

            try
            {
                _logger.LogInformation("Serving video from: {FilePath}", fullPath);
                var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
                var extension = Path.GetExtension(fullPath).ToLower();
                string mimeType = extension switch
                {
                    ".webm" => "video/webm",
                    ".mp4" => "video/mp4",
                    _ => "application/octet-stream"
                };
                return File(stream, mimeType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accessing interview video file at path: {FilePath}", fullPath);
                return NotFound("Error accessing video file.");
            }
        }

        [HttpGet]
        public IActionResult ManageTemplates()
        {
            var templates = _chatDbService.GetMessageTemplates();
            return View(templates);
        }

        [HttpPost]
        public IActionResult SaveTemplate(int? templateId, string templateName, string messageType, string templateContent, bool isDefault)
        {
            try
            {
                if (isDefault)
                {
                    using var conn = new SqlConnection(_connectionString);
                    conn.Open();
                    var cmd = new SqlCommand("UPDATE MessageTemplates SET IsDefault = 0 WHERE MessageType = @MessageType", conn);
                    cmd.Parameters.AddWithValue("@MessageType", messageType);
                    cmd.ExecuteNonQuery();
                }

                if (templateId.HasValue)
                {
                    _chatDbService.UpdateMessageTemplate(templateId.Value, templateName, messageType, templateContent, isDefault);
                }
                else
                {
                    _chatDbService.SaveMessageTemplate(templateName, messageType, templateContent, isDefault);
                }
                return RedirectToAction("ManageTemplates");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving template: {TemplateName}", templateName);
                ViewBag.ErrorMessage = "Error saving template. Please try again.";
                return View("ManageTemplates", _chatDbService.GetMessageTemplates());
            }
        }

        [HttpPost]
        public async Task<IActionResult> SendPassMessage(string userId, string templateName)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand(@"
                    SELECT u.Name, u.Email, u.Phone, u.EmailSent, u.WhatsAppSent, i.JobTitle
                    FROM Users u
                    LEFT JOIN Interactions i ON u.UserId = i.UserId AND i.Questions IS NOT NULL
                    WHERE u.UserId = @UserId 
                    AND (i.InteractionId = (
                        SELECT MAX(InteractionId) 
                        FROM Interactions 
                        WHERE UserId = u.UserId 
                        AND Questions IS NOT NULL
                    ) OR i.InteractionId IS NULL)", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var name = reader["Name"] != DBNull.Value ? reader["Name"].ToString() : "Candidate";
                    var email = reader["Email"] != DBNull.Value ? reader["Email"].ToString() : null;
                    var phone = reader["Phone"] != DBNull.Value ? reader["Phone"].ToString() : null;
                    var jobTitle = reader["JobTitle"] != DBNull.Value ? reader["JobTitle"].ToString() : "the position";
                    var emailSent = reader["EmailSent"] != DBNull.Value ? (bool)reader["EmailSent"] : false;
                    var whatsappSent = reader["WhatsAppSent"] != DBNull.Value ? (bool)reader["WhatsAppSent"] : false;
                    reader.Close();

                    var templates = _chatDbService.GetMessageTemplates() ?? new List<MessageTemplate>();
                    var template = templates.FirstOrDefault(t => t.TemplateName == templateName && t.MessageType == "Email");
                    if (template != null && !string.IsNullOrEmpty(email))
                    {
                        var body = template.TemplateContent.Replace("{Name}", name).Replace("{JobTitle}", jobTitle);
                        await _notificationService.SendEmailAsync(email, "Interview Result", body);
                        _chatDbService.UpdateUserMessageStatus(userId, true, whatsappSent);
                        TempData["SuccessMessage"] = $"Email sent successfully to {email}.";
                    }
                    else if (template != null && string.IsNullOrEmpty(email))
                    {
                        TempData["ErrorMessage"] = "No email address available for this user.";
                    }
                    else if (template == null && templates.Any(t => t.TemplateName == templateName && t.MessageType == "WhatsApp"))
                    {
                        template = templates.FirstOrDefault(t => t.TemplateName == templateName && t.MessageType == "WhatsApp");
                        if (template != null && !string.IsNullOrEmpty(phone) && !whatsappSent)
                        {
                            var body = template.TemplateContent.Replace("{Name}", name).Replace("{JobTitle}", jobTitle);
                            await _notificationService.SendWhatsAppAsync(phone, body);
                            _chatDbService.UpdateUserMessageStatus(userId, emailSent, true);
                            TempData["SuccessMessage"] = $"{TempData["SuccessMessage"] ?? ""} WhatsApp message sent successfully to {phone}.";
                        }
                        else if (template != null && string.IsNullOrEmpty(phone))
                        {
                            TempData["ErrorMessage"] = $"{TempData["ErrorMessage"] ?? ""} No phone number available for this user.";
                        }
                        else if (template != null && whatsappSent)
                        {
                            TempData["ErrorMessage"] = $"{TempData["ErrorMessage"] ?? ""} WhatsApp message already sent to this user.";
                        }
                        else
                        {
                            TempData["ErrorMessage"] = $"{TempData["ErrorMessage"] ?? ""} WhatsApp template '{templateName}' not found.";
                        }
                    }
                    else
                    {
                        TempData["ErrorMessage"] = $"Template '{templateName}' not found.";
                    }
                }
                else
                {
                    TempData["ErrorMessage"] = "User not found.";
                }
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending pass message for UserId: {UserId}", userId);
                TempData["ErrorMessage"] = "Error sending message. Please try again.";
                return RedirectToAction("Index");
            }
        }

        [HttpGet]
        public IActionResult ViewConversation(string userId)
        {
            var conversations = new List<ConversationViewModel>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand(@"
                    SELECT u.Name, u.Phone, u.Email, u.Experience, u.EmploymentStatus, u.Reason, i.ConversationText, i.CreatedAt,
                           COALESCE((
                               SELECT TOP 1 TabSwitchCount 
                               FROM Interactions i2 
                               WHERE i2.UserId = u.UserId 
                               AND i2.Questions IS NOT NULL 
                               AND i2.IsComplete = 1 
                               ORDER BY i2.CreatedAt DESC
                           ), 0) AS TabSwitchCount
                    FROM Interactions i
                    JOIN Users u ON i.UserId = u.UserId
                    WHERE i.UserId = @UserId 
                    AND i.ConversationText IS NOT NULL 
                    AND i.Questions IS NULL
                    ORDER BY i.CreatedAt ASC", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var conversation = new ConversationViewModel
                    {
                        UserId = userId,
                        Name = reader["Name"] != DBNull.Value ? reader["Name"].ToString() : string.Empty,
                        Phone = reader["Phone"] != DBNull.Value ? reader["Phone"].ToString() : string.Empty,
                        Email = reader["Email"] != DBNull.Value ? reader["Email"].ToString() : string.Empty,
                        Experience = reader["Experience"] != DBNull.Value ? reader["Experience"].ToString() : string.Empty,
                        EmploymentStatus = reader["EmploymentStatus"] != DBNull.Value ? reader["EmploymentStatus"].ToString() : string.Empty,
                        Reason = reader["Reason"] != DBNull.Value ? reader["Reason"].ToString() : string.Empty,
                        ConversationText = reader["ConversationText"] != DBNull.Value ? reader["ConversationText"].ToString() : string.Empty,
                        CreatedAt = (DateTime)reader["CreatedAt"],
                        TabSwitchCount = (int)reader["TabSwitchCount"]
                    };
                    conversations.Add(conversation);
                }
                if (conversations.Count == 0)
                {
                    ViewBag.ErrorMessage = "No conversation history found for this user.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving conversation history for UserId: {UserId}", userId);
                ViewBag.ErrorMessage = "Error retrieving conversation history.";
            }
            return View("ViewConversation", conversations);
        }
    }

    public class UserAdminViewModel
    {
        public string UserId { get; set; }
        public string Name { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string IDProofPath { get; set; }
        public string IDProofType { get; set; }
        public int InterviewCount { get; set; }
        public string InterviewStatus { get; set; }
        public string InterviewVideoPath { get; set; }
        public bool EmailSent { get; set; }
        public bool WhatsAppSent { get; set; }
    }

    public class ConversationViewModel
    {
        public string UserId { get; set; }
        public string Name { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string ConversationText { get; set; }
        public DateTime CreatedAt { get; set; }
        public int TabSwitchCount { get; set; }
        public string Experience { get; set; }
        public string EmploymentStatus { get; set; }
        public string Reason { get; set; }
    }
}