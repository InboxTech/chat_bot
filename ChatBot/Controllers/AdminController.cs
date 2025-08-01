using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChatBot.Models;
using Newtonsoft.Json;

namespace ChatBot.Controllers
{
    public class AdminController : Controller
    {
        private readonly string _connectionString;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AdminController> _logger;

        public AdminController(IConfiguration configuration, ILogger<AdminController> logger)
        {
            _configuration = configuration;
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _logger = logger;
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

                var tableCheckCmd = new SqlCommand(@"
                    SELECT COUNT(*) 
                    FROM INFORMATION_SCHEMA.TABLES 
                    WHERE TABLE_NAME IN ('Users', 'Interactions')", conn);
                int tableCount = (int)tableCheckCmd.ExecuteScalar();
                if (tableCount < 2)
                {
                    var errorMsg = "Required tables 'Users' or 'Interactions' are missing in the database.";
                    _logger.LogError(errorMsg);
                    ViewBag.ErrorMessage = errorMsg;
                    return View(userDetailsList);
                }

                var userColumnCheckCmd = new SqlCommand(@"
                    SELECT COUNT(*) 
                    FROM INFORMATION_SCHEMA.COLUMNS 
                    WHERE TABLE_NAME = 'Users' 
                    AND COLUMN_NAME IN ('UserId', 'Name', 'Phone', 'Email', 'Experience', 
                                        'EmploymentStatus', 'Reason', 'CreatedAt', 
                                        'IDProofPath', 'IDProofType')", conn);
                int userColumnCount = (int)userColumnCheckCmd.ExecuteScalar();
                if (userColumnCount < 10)
                {
                    var errorMsg = "One or more required columns are missing in the Users table.";
                    _logger.LogError(errorMsg);
                    ViewBag.ErrorMessage = errorMsg;
                    return View(userDetailsList);
                }

                var interactionColumnCheckCmd = new SqlCommand(@"
                    SELECT COUNT(*) 
                    FROM INFORMATION_SCHEMA.COLUMNS 
                    WHERE TABLE_NAME = 'Interactions' 
                    AND COLUMN_NAME IN ('InteractionId', 'UserId', 'InteractionType', 'JobTitle', 
                                        'QuestionIndex', 'Questions', 'Answers', 'IsComplete', 
                                        'IsSubmitted', 'TabSwitchCount', 'ConversationText', 
                                        'UserMessage', 'BotResponse', 'Model', 'CreatedAt')", conn);
                int interactionColumnCount = (int)interactionColumnCheckCmd.ExecuteScalar();
                if (interactionColumnCount < 15)
                {
                    var errorMsg = "One or more required columns are missing in the Interactions table.";
                    _logger.LogError(errorMsg);
                    ViewBag.ErrorMessage = errorMsg;
                    return View(userDetailsList);
                }

                var cmd = new SqlCommand(@"
                    SELECT u.UserId, u.Name, u.Phone, u.Email, u.Experience, u.EmploymentStatus, 
                           u.Reason, u.CreatedAt, u.IDProofPath, u.IDProofType,
                           ISNULL((SELECT COUNT(*) FROM Interactions WHERE UserId = u.UserId AND InteractionType = 'Interview' AND IsComplete = 1), 0) AS InterviewCount,
                           ISNULL((SELECT TOP 1 IsSubmitted FROM Interactions WHERE UserId = u.UserId AND InteractionType = 'Interview' AND IsComplete = 1 ORDER BY CreatedAt DESC), 0) AS IsSubmitted
                    FROM Users u", conn);

                using var reader = cmd.ExecuteReader();
                var userDict = new Dictionary<string, UserAdminViewModel>();
                while (reader.Read())
                {
                    var user = new UserAdminViewModel
                    {
                        UserId = reader["UserId"].ToString(),
                        Name = reader["Name"] != DBNull.Value ? reader["Name"].ToString() : string.Empty,
                        Phone = reader["Phone"] != DBNull.Value ? reader["Phone"].ToString() : string.Empty,
                        Email = reader["Email"] != DBNull.Value ? reader["Email"].ToString() : string.Empty,
                        Experience = reader["Experience"] != DBNull.Value ? reader["Experience"].ToString() : string.Empty,
                        EmploymentStatus = reader["EmploymentStatus"] != DBNull.Value ? reader["EmploymentStatus"].ToString() : string.Empty,
                        Reason = reader["Reason"] != DBNull.Value ? reader["Reason"].ToString() : string.Empty,
                        CreatedAt = reader["CreatedAt"] != DBNull.Value ? (DateTime?)reader["CreatedAt"] : null,
                        IDProofPath = reader["IDProofPath"] != DBNull.Value ? reader["IDProofPath"].ToString() : string.Empty,
                        IDProofType = reader["IDProofType"] != DBNull.Value ? reader["IDProofType"].ToString() : string.Empty,
                        InterviewCount = (int)reader["InterviewCount"],
                        IsInterviewSubmitted = (bool)reader["IsSubmitted"],
                        InterviewVideoPath = string.Empty,
                        CompanyQueries = new List<string>()
                    };
                    userDict[user.UserId] = user;
                    userDetailsList.Add(user);
                }
                reader.Close();

                var queryCmd = new SqlCommand(@"
                    SELECT UserId, UserMessage
                    FROM Interactions
                    WHERE InteractionType = 'Chat' AND UserMessage IS NOT NULL 
                    AND BotResponse NOT LIKE '%Question%'
                    AND BotResponse NOT LIKE '%interview for%'
                    AND BotResponse NOT LIKE '%upload your resume%'
                    AND BotResponse NOT LIKE '%Please provide%'", conn);

                using var queryReader = queryCmd.ExecuteReader();
                while (queryReader.Read())
                {
                    var userId = queryReader["UserId"].ToString();
                    var userMessage = queryReader["UserMessage"] != DBNull.Value ? queryReader["UserMessage"].ToString() : string.Empty;
                    if (userDict.ContainsKey(userId))
                    {
                        userDict[userId].CompanyQueries.Add(userMessage);
                    }
                }
            }
            catch (SqlException sqlEx)
            {
                var errorMsg = $"SQL Error retrieving user data: {sqlEx.Message}, Error Code: {sqlEx.Number}, Line: {sqlEx.LineNumber}";
                _logger.LogError(sqlEx, errorMsg);
                ViewBag.ErrorMessage = "Database error occurred. Please check the server logs or try again later.";
                return View(userDetailsList);
            }
            catch (Exception ex)
            {
                var errorMsg = $"Error retrieving user data: {ex.Message}";
                _logger.LogError(ex, errorMsg);
                ViewBag.ErrorMessage = "An unexpected error occurred. Please check the server logs or try again later.";
                return View(userDetailsList);
            }

            return View(userDetailsList);
        }

        [HttpGet]
        public IActionResult ViewConversation(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("ViewConversation called with null or empty userId.");
                return NotFound("User ID is required.");
            }

            var conversations = new List<ConversationViewModel>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    SELECT u.Name, u.Phone, u.Email, i.ConversationText, i.CreatedAt
                    FROM Interactions i
                    JOIN Users u ON i.UserId = u.UserId
                    WHERE i.UserId = @UserId AND i.InteractionType = 'Chat' AND i.ConversationText IS NOT NULL
                    ORDER BY i.CreatedAt ASC", conn); // Changed to ASC for chronological order
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
                        ConversationText = reader["ConversationText"] != DBNull.Value ? reader["ConversationText"].ToString() : string.Empty,
                        CreatedAt = (DateTime)reader["CreatedAt"]
                    };
                    conversations.Add(conversation);
                }

                if (conversations.Count == 0)
                {
                    _logger.LogInformation("No conversation history found for UserId: {UserId}", userId);
                    ViewBag.ErrorMessage = "No conversation history found for this user.";
                }
            }
            catch (SqlException sqlEx)
            {
                var errorMsg = $"SQL Error retrieving conversation history for UserId: {userId}, Error: {sqlEx.Message}, Error Code: {sqlEx.Number}";
                _logger.LogError(sqlEx, errorMsg);
                ViewBag.ErrorMessage = "Database error occurred while retrieving conversation history. Please check the server logs or try again later.";
            }
            catch (Exception ex)
            {
                var errorMsg = $"Error retrieving conversation history for UserId: {userId}, Error: {ex.Message}";
                _logger.LogError(ex, errorMsg);
                ViewBag.ErrorMessage = "An unexpected error occurred while retrieving conversation history. Please check the server logs or try again later.";
            }

            return View(conversations);
        }

        [HttpGet]
        public IActionResult ViewIDProof(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("ID proof not found at path: {FilePath}", filePath);
                return NotFound("ID proof not found.");
            }

            var extension = Path.GetExtension(filePath).ToLower();
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
                var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                return File(stream, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accessing ID proof file at path: {FilePath}", filePath);
                return NotFound("Error accessing ID proof file.");
            }
        }

        [HttpGet]
        public IActionResult ViewInterviewVideo(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("Interview video not found at path: {FilePath}", filePath);
                return NotFound("Video not found.");
            }

            try
            {
                var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                return File(stream, "video/webm");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accessing interview video file at path: {FilePath}", filePath);
                return NotFound("Error accessing video file.");
            }
        }
    }

    public class UserAdminViewModel
    {
        public string UserId { get; set; }
        public string Name { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string Experience { get; set; }
        public string EmploymentStatus { get; set; }
        public string Reason { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string IDProofPath { get; set; }
        public string IDProofType { get; set; }
        public int InterviewCount { get; set; }
        public bool IsInterviewSubmitted { get; set; }
        public string InterviewVideoPath { get; set; }
        public List<string> CompanyQueries { get; set; }
    }

    public class ConversationViewModel
    {
        public string UserId { get; set; }
        public string Name { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string ConversationText { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}