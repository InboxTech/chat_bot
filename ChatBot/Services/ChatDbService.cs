using Microsoft.Data.SqlClient;
using ChatBot.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatBot.Services
{
    public class ChatDbService
    {
        private readonly string _connectionString;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<ChatDbService> _logger;
        private readonly string _interviewVideoFolder;

        public ChatDbService(IConfiguration config, IHttpContextAccessor httpContextAccessor, ILogger<ChatDbService> logger)
        {
            _connectionString = config.GetConnectionString("DefaultConnection");
            _interviewVideoFolder = config.GetSection("UploadPaths:InterviewVideoFolder").Value;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public void SaveUserDetails(UserDetails user)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var checkCmd = new SqlCommand("SELECT COUNT(*) FROM Users WHERE UserId = @UserId", conn);
                checkCmd.Parameters.AddWithValue("@UserId", user.UserId ?? (object)DBNull.Value);
                bool userExists = (int)checkCmd.ExecuteScalar() > 0;

                SqlCommand cmd;
                if (userExists)
                {
                    cmd = new SqlCommand(@"
                        UPDATE Users
                        SET Name = @Name, Phone = @Phone, Email = @Email, Experience = @Experience, 
                            EmploymentStatus = @EmploymentStatus, Reason = @Reason, CreatedAt = @CreatedAt, 
                            IDProofPath = @IDProofPath, IDProofType = @IDProofType
                        WHERE UserId = @UserId", conn);
                }
                else
                {
                    cmd = new SqlCommand(@"
                        INSERT INTO Users (UserId, Name, Phone, Email, Experience, EmploymentStatus, Reason, 
                            CreatedAt, IDProofPath, IDProofType)
                        VALUES (@UserId, @Name, @Phone, @Email, @Experience, @EmploymentStatus, @Reason, 
                            @CreatedAt, @IDProofPath, @IDProofType)", conn);
                }

                cmd.Parameters.AddWithValue("@UserId", user.UserId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Name", user.Name ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", user.Phone ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", user.Email ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Experience", user.Experience ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@EmploymentStatus", user.EmploymentStatus ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Reason", user.Reason ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedAt", user.CreatedAt == default ? DateTime.Now : user.CreatedAt);
                cmd.Parameters.AddWithValue("@IDProofPath", user.IDProofPath ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@IDProofType", user.IDProofType ?? (object)DBNull.Value);

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving user details for UserId: {UserId}", user.UserId);
                throw;
            }
        }

        public int GetInterviewAttemptCount(string name, string email, string phone, DateTime? dateOfBirth)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    SELECT COUNT(*) 
                    FROM Interactions 
                    WHERE InteractionType = 'Interview' AND IsComplete = 1
                    AND UserId IN (
                        SELECT UserId 
                        FROM Users 
                        WHERE (Name = @Name OR @Name IS NULL)
                        AND (Email = @Email OR @Email IS NULL)
                        AND (Phone = @Phone OR @Phone IS NULL)
                    )", conn);

                cmd.Parameters.AddWithValue("@Name", string.IsNullOrEmpty(name) ? (object)DBNull.Value : name);
                cmd.Parameters.AddWithValue("@Email", string.IsNullOrEmpty(email) ? (object)DBNull.Value : email);
                cmd.Parameters.AddWithValue("@Phone", string.IsNullOrEmpty(phone) ? (object)DBNull.Value : phone);

                return (int)cmd.ExecuteScalar();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error counting interview attempts for Name: {Name}, Email: {Email}, Phone: {Phone}", name, email, phone);
                throw;
            }
        }

        public void MarkInterviewAsSubmitted(int interactionId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    UPDATE Interactions 
                    SET IsSubmitted = 1
                    WHERE InteractionId = @InteractionId AND InteractionType = 'Interview'", conn);

                cmd.Parameters.AddWithValue("@InteractionId", interactionId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking interview session as submitted for InteractionId: {InteractionId}", interactionId);
                throw;
            }
        }

        public void SaveMessage(ChatMessage message)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    INSERT INTO Interactions (UserId, InteractionType, UserMessage, BotResponse, Model, CreatedAt)
                    VALUES (@UserId, 'Chat', @UserMessage, @BotResponse, @Model, @CreatedAt)", conn);

                cmd.Parameters.AddWithValue("@UserId", message.UserId ?? "");
                cmd.Parameters.AddWithValue("@UserMessage", message.UserMessage ?? "");
                cmd.Parameters.AddWithValue("@BotResponse", message.BotResponse ?? "");
                cmd.Parameters.AddWithValue("@Model", message.Model ?? "custom");
                cmd.Parameters.AddWithValue("@CreatedAt", message.CreatedAt);

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving message for UserId: {UserId}", message.UserId);
                throw;
            }
        }

        public InterviewSession? GetLatestSession(string userId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    SELECT TOP 1 * FROM Interactions 
                    WHERE UserId = @UserId AND InteractionType = 'Interview'
                    ORDER BY CreatedAt DESC", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new InterviewSession
                    {
                        Id = (int)reader["InteractionId"],
                        UserId = (string)reader["UserId"],
                        JobTitle = reader["JobTitle"] != DBNull.Value ? (string)reader["JobTitle"] : "",
                        QuestionIndex = reader["QuestionIndex"] != DBNull.Value ? (int)reader["QuestionIndex"] : 0,
                        Questions = JsonConvert.DeserializeObject<List<string>>((string)reader["Questions"] ?? "[]") ?? new(),
                        Answers = JsonConvert.DeserializeObject<List<string>>((string)reader["Answers"] ?? "[]") ?? new(),
                        IsComplete = reader["IsComplete"] != DBNull.Value ? (bool)reader["IsComplete"] : false,
                        IsSubmitted = reader["IsSubmitted"] != DBNull.Value ? (bool)reader["IsSubmitted"] : false,
                        TabSwitchCount = reader["TabSwitchCount"] != DBNull.Value ? (int)reader["TabSwitchCount"] : 0,
                        VideoPath = reader["VideoPath"] != DBNull.Value ? (string)reader["VideoPath"] : null,
                        CreatedAt = (DateTime)reader["CreatedAt"]
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving latest session for UserId: {UserId}", userId);
                throw;
            }
        }

        //public void UpdateInterviewSession(InterviewSession session)
        //{
        //    try
        //    {
        //        using var conn = new SqlConnection(_connectionString);
        //        conn.Open();

        //        var cmd = new SqlCommand(@"
        //            UPDATE Interactions 
        //            SET QuestionIndex = @QuestionIndex, 
        //                Questions = @Questions, 
        //                Answers = @Answers,
        //                IsComplete = @IsComplete,
        //                TabSwitchCount = @TabSwitchCount,
        //                VideoPath = @VideoPath
        //            WHERE InteractionId = @InteractionId AND InteractionType = 'Interview'", conn);

        //        cmd.Parameters.AddWithValue("@InteractionId", session.Id);
        //        cmd.Parameters.AddWithValue("@QuestionIndex", session.QuestionIndex);
        //        cmd.Parameters.AddWithValue("@Questions", JsonConvert.SerializeObject(session.Questions));
        //        cmd.Parameters.AddWithValue("@Answers", JsonConvert.SerializeObject(session.Answers));
        //        cmd.Parameters.AddWithValue("@IsComplete", session.IsComplete);
        //        cmd.Parameters.AddWithValue("@TabSwitchCount", session.TabSwitchCount);
        //        cmd.Parameters.AddWithValue("@VideoPath", session.VideoPath ?? (object)DBNull.Value);

        //        cmd.ExecuteNonQuery();
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error updating interview session for InteractionId: {InteractionId}", session.Id);
        //        throw;
        //    }
        //}

        public void UpdateTabSwitchCount(string userId, int count)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    UPDATE Interactions 
                    SET TabSwitchCount = COALESCE(TabSwitchCount, 0) + @Count
                    WHERE UserId = @UserId AND InteractionType = 'Interview' AND IsComplete = 0", conn);

                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@Count", count);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating tab switch count for UserId: {UserId}", userId);
                throw;
            }
        }

        public void SaveFullConversation(string userId, string name, string phone, string email, List<ChatMessage> messages)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var conversationText = JsonConvert.SerializeObject(messages);
                var cmd = new SqlCommand(
                    @"INSERT INTO Interactions (
                        UserId, InteractionType, ConversationText, CreatedAt
                    ) VALUES (
                        @UserId, @InteractionType, @ConversationText, @CreatedAt
                    )", conn);

                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@InteractionType", "Chat");
                cmd.Parameters.AddWithValue("@ConversationText", conversationText);
                cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now);

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error saving full conversation for UserId: {userId}", ex);
            }
        }

        //public void SaveInterviewSession(InterviewSession session)
        //{
        //    try
        //    {
        //        using var conn = new SqlConnection(_connectionString);
        //        conn.Open();

        //        var cmd = new SqlCommand(
        //            @"INSERT INTO Interactions (
        //                UserId, InteractionType, JobTitle, QuestionIndex, 
        //                Questions, Answers, IsComplete, IsSubmitted, 
        //                TabSwitchCount, VideoPath, CreatedAt
        //            ) VALUES (
        //                @UserId, @InteractionType, @JobTitle, @QuestionIndex, 
        //                @Questions, @Answers, @IsComplete, @IsSubmitted, 
        //                @TabSwitchCount, @VideoPath, @CreatedAt
        //            ); SELECT SCOPE_IDENTITY();", conn);

        //        cmd.Parameters.AddWithValue("@UserId", session.UserId);
        //        cmd.Parameters.AddWithValue("@InteractionType", "Interview");
        //        cmd.Parameters.AddWithValue("@JobTitle", (object)session.JobTitle ?? DBNull.Value);
        //        cmd.Parameters.AddWithValue("@QuestionIndex", session.QuestionIndex);
        //        cmd.Parameters.AddWithValue("@Questions", JsonConvert.SerializeObject(session.Questions));
        //        cmd.Parameters.AddWithValue("@Answers", JsonConvert.SerializeObject(session.Answers));
        //        cmd.Parameters.AddWithValue("@IsComplete", session.IsComplete);
        //        cmd.Parameters.AddWithValue("@IsSubmitted", session.IsSubmitted);
        //        cmd.Parameters.AddWithValue("@TabSwitchCount", session.TabSwitchCount);
        //        cmd.Parameters.AddWithValue("@VideoPath", session.VideoPath ?? (object)DBNull.Value);
        //        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now);

        //        session.Id = Convert.ToInt32(cmd.ExecuteScalar());
        //    }
        //    catch (Exception ex)
        //    {
        //        throw new Exception($"Error saving interview session for UserId: {session.UserId}", ex);
        //    }
        //}

        public void SaveInterviewSession(InterviewSession session)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                // Validate VideoPath
                if (!string.IsNullOrEmpty(session.VideoPath) && !session.VideoPath.Contains(_interviewVideoFolder))
                {
                    session.VideoPath = Path.Combine(Directory.GetCurrentDirectory(), _interviewVideoFolder, session.VideoPath);
                    _logger.LogInformation("Normalized VideoPath to: {VideoPath}", session.VideoPath);
                }

                var cmd = new SqlCommand(
                    @"INSERT INTO Interactions (
                    UserId, InteractionType, JobTitle, QuestionIndex,
                    Questions, Answers, IsComplete, IsSubmitted,
                    TabSwitchCount, VideoPath, CreatedAt
                ) VALUES (
                    @UserId, @InteractionType, @JobTitle, @QuestionIndex,
                    @Questions, @Answers, @IsComplete, @IsSubmitted,
                    @TabSwitchCount, @VideoPath, @CreatedAt
                ); SELECT SCOPE_IDENTITY();", conn);

                cmd.Parameters.AddWithValue("@UserId", session.UserId);
                cmd.Parameters.AddWithValue("@InteractionType", "Interview");
                cmd.Parameters.AddWithValue("@JobTitle", (object)session.JobTitle ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@QuestionIndex", session.QuestionIndex);
                cmd.Parameters.AddWithValue("@Questions", JsonConvert.SerializeObject(session.Questions));
                cmd.Parameters.AddWithValue("@Answers", JsonConvert.SerializeObject(session.Answers));
                cmd.Parameters.AddWithValue("@IsComplete", session.IsComplete);
                cmd.Parameters.AddWithValue("@IsSubmitted", session.IsSubmitted);
                cmd.Parameters.AddWithValue("@TabSwitchCount", session.TabSwitchCount);
                cmd.Parameters.AddWithValue("@VideoPath", session.VideoPath ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now);

                session.Id = Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving interview session for UserId: {UserId}", session.UserId);
                throw;
            }
        }

        public void UpdateInterviewSession(InterviewSession session)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                // Validate VideoPath
                if (!string.IsNullOrEmpty(session.VideoPath) && !session.VideoPath.Contains(_interviewVideoFolder))
                {
                    session.VideoPath = Path.Combine(Directory.GetCurrentDirectory(), _interviewVideoFolder, session.VideoPath);
                    _logger.LogInformation("Normalized VideoPath to: {VideoPath}", session.VideoPath);
                }

                var cmd = new SqlCommand(@"
                UPDATE Interactions
                SET QuestionIndex = @QuestionIndex,
                    Questions = @Questions,
                    Answers = @Answers,
                    IsComplete = @IsComplete,
                    IsSubmitted = @IsSubmitted,
                    TabSwitchCount = @TabSwitchCount,
                    VideoPath = @VideoPath
                WHERE InteractionId = @InteractionId AND InteractionType = 'Interview'", conn);

                cmd.Parameters.AddWithValue("@InteractionId", session.Id);
                cmd.Parameters.AddWithValue("@QuestionIndex", session.QuestionIndex);
                cmd.Parameters.AddWithValue("@Questions", JsonConvert.SerializeObject(session.Questions));
                cmd.Parameters.AddWithValue("@Answers", JsonConvert.SerializeObject(session.Answers));
                cmd.Parameters.AddWithValue("@IsComplete", session.IsComplete);
                cmd.Parameters.AddWithValue("@IsSubmitted", session.IsSubmitted);
                cmd.Parameters.AddWithValue("@TabSwitchCount", session.TabSwitchCount);
                cmd.Parameters.AddWithValue("@VideoPath", session.VideoPath ?? (object)DBNull.Value);

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating interview session for InteractionId: {InteractionId}", session.Id);
                throw;
            }
        }

        public void SaveMessageTemplate(string templateName, string messageType, string templateContent, bool isDefault)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand(@"
                    INSERT INTO MessageTemplates (TemplateName, MessageType, TemplateContent, IsDefault, CreatedAt, UpdatedAt)
                    VALUES (@TemplateName, @MessageType, @TemplateContent, @IsDefault, GETDATE(), GETDATE())", conn);
                cmd.Parameters.AddWithValue("@TemplateName", templateName);
                cmd.Parameters.AddWithValue("@MessageType", messageType);
                cmd.Parameters.AddWithValue("@TemplateContent", templateContent);
                cmd.Parameters.AddWithValue("@IsDefault", isDefault);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving message template: {TemplateName}", templateName);
                throw;
            }
        }

        public void UpdateMessageTemplate(int templateId, string templateName, string messageType, string templateContent, bool isDefault)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand(@"
                    UPDATE MessageTemplates
                    SET TemplateName = @TemplateName, MessageType = @MessageType, TemplateContent = @TemplateContent, 
                        IsDefault = @IsDefault, UpdatedAt = GETDATE()
                    WHERE TemplateId = @TemplateId", conn);
                cmd.Parameters.AddWithValue("@TemplateId", templateId);
                cmd.Parameters.AddWithValue("@TemplateName", templateName);
                cmd.Parameters.AddWithValue("@MessageType", messageType);
                cmd.Parameters.AddWithValue("@TemplateContent", templateContent);
                cmd.Parameters.AddWithValue("@IsDefault", isDefault);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating message template: {TemplateId}", templateId);
                throw;
            }
        }

        public List<MessageTemplate> GetMessageTemplates()
        {
            try
            {
                var templates = new List<MessageTemplate>();
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand("SELECT TemplateName, MessageType, TemplateContent, IsDefault FROM MessageTemplates", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    templates.Add(new MessageTemplate
                    {
                        TemplateName = reader["TemplateName"] != DBNull.Value ? reader["TemplateName"].ToString() : string.Empty,
                        MessageType = reader["MessageType"] != DBNull.Value ? reader["MessageType"].ToString() : string.Empty,
                        TemplateContent = reader["TemplateContent"] != DBNull.Value ? reader["TemplateContent"].ToString() : string.Empty,
                        IsDefault = reader["IsDefault"] != DBNull.Value ? (bool)reader["IsDefault"] : false
                    });
                }
                _logger.LogInformation("Retrieved {Count} message templates", templates.Count);
                return templates;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving message templates");
                return new List<MessageTemplate>();
            }
        }

        public void UpdateUserMessageStatus(string userId, bool emailSent, bool whatsappSent)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand("UPDATE Users SET EmailSent = @EmailSent, WhatsAppSent = @WhatsAppSent WHERE UserId = @UserId", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@EmailSent", emailSent);
                cmd.Parameters.AddWithValue("@WhatsAppSent", whatsappSent);
                cmd.ExecuteNonQuery();
                _logger.LogInformation("Updated message status for UserId: {UserId}, EmailSent: {EmailSent}, WhatsAppSent: {WhatsAppSent}", userId, emailSent, whatsappSent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating message status for UserId: {UserId}", userId);
                throw;
            }
        }

        // Add method to fetch user message status
        public (bool EmailSent, bool SMSSent) GetUserMessageStatus(string userId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                var cmd = new SqlCommand("SELECT EmailSent, SMSSent FROM Users WHERE UserId = @UserId", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return ((bool)reader["EmailSent"], (bool)reader["SMSSent"]);
                }
                return (false, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving message status for UserId: {UserId}", userId);
                throw;
            }
        }
    }
}