using Microsoft.Data.SqlClient;
using ChatBot.Models;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace ChatBot.Services
{
    public class ChatDbService
    {
        private readonly string _connectionString;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<ChatDbService> _logger;

        public ChatDbService(IConfiguration config, IHttpContextAccessor httpContextAccessor, ILogger<ChatDbService> logger)
        {
            _connectionString = config.GetConnectionString("DefaultConnection");
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public void SaveUserDetails(UserDetails user)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                var cmd = new SqlCommand(@"
                    INSERT INTO UserDetails (UserId, Name, Phone, Email, Experience, EmploymentStatus, Reason, CreatedAt, IDProofPath, DateOfBirth)
                    VALUES (@UserId, @Name, @Phone, @Email, @Experience, @EmploymentStatus, @Reason, @CreatedAt, @IDProofPath, @DateOfBirth)", conn);

                cmd.Parameters.AddWithValue("@UserId", user.UserId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Name", user.Name ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", user.Phone ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", user.Email ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Experience", user.Experience ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@EmploymentStatus", user.EmploymentStatus ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Reason", user.Reason ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedAt", user.CreatedAt == default ? DateTime.Now : user.CreatedAt);
                cmd.Parameters.AddWithValue("@IDProofPath", user.IDProofPath ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@DateOfBirth", user.DateOfBirth.HasValue ? user.DateOfBirth.Value : (object)DBNull.Value);

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
                    FROM InterviewSessions 
                    WHERE UserId IN (
                        SELECT UserId 
                        FROM UserDetails 
                        WHERE (Name = @Name OR @Name IS NULL)
                        AND (Email = @Email OR @Email IS NULL)
                        AND (Phone = @Phone OR @Phone IS NULL)
                        AND (DateOfBirth = @DateOfBirth OR @DateOfBirth IS NULL)
                    )", conn);

                cmd.Parameters.AddWithValue("@Name", string.IsNullOrEmpty(name) ? (object)DBNull.Value : name);
                cmd.Parameters.AddWithValue("@Email", string.IsNullOrEmpty(email) ? (object)DBNull.Value : email);
                cmd.Parameters.AddWithValue("@Phone", string.IsNullOrEmpty(phone) ? (object)DBNull.Value : phone);
                cmd.Parameters.AddWithValue("@DateOfBirth", dateOfBirth.HasValue ? dateOfBirth.Value : (object)DBNull.Value);

                return (int)cmd.ExecuteScalar();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error counting interview attempts for Name: {Name}, Email: {Email}, Phone: {Phone}, DOB: {DateOfBirth}", name, email, phone, dateOfBirth);
                throw;
            }
        }

        public void SaveMessage(ChatMessage message)
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            var cmd = new SqlCommand(@"
                INSERT INTO ChatMessages (UserId, UserMessage, BotResponse, Model, CreatedAt)
                VALUES (@userId, @user, @bot, @model, @time)", conn);

            cmd.Parameters.AddWithValue("@userId", message.UserId ?? "");
            cmd.Parameters.AddWithValue("@user", message.UserMessage ?? "");
            cmd.Parameters.AddWithValue("@bot", message.BotResponse ?? "");
            cmd.Parameters.AddWithValue("@model", message.Model ?? "custom");
            cmd.Parameters.AddWithValue("@time", message.CreatedAt);

            cmd.ExecuteNonQuery();
        }

        public void SaveInterviewSession(InterviewSession session)
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            var cmd = new SqlCommand(@"
                INSERT INTO InterviewSessions (UserId, JobTitle, QuestionIndex, Questions, Answers, IsComplete, TabSwitchCount, CreatedAt)
                VALUES (@UserId, @JobTitle, @QuestionIndex, @Questions, @Answers, @IsComplete, @TabSwitchCount, @CreatedAt);
                SELECT SCOPE_IDENTITY();", conn);

            cmd.Parameters.AddWithValue("@UserId", session.UserId);
            cmd.Parameters.AddWithValue("@JobTitle", session.JobTitle);
            cmd.Parameters.AddWithValue("@QuestionIndex", session.QuestionIndex);
            cmd.Parameters.AddWithValue("@Questions", JsonConvert.SerializeObject(session.Questions));
            cmd.Parameters.AddWithValue("@Answers", JsonConvert.SerializeObject(session.Answers));
            cmd.Parameters.AddWithValue("@IsComplete", session.IsComplete);
            cmd.Parameters.AddWithValue("@TabSwitchCount", session.TabSwitchCount);
            cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now);

            session.Id = Convert.ToInt32(cmd.ExecuteScalar());
        }

        public InterviewSession? GetLatestSession(string userId)
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            var cmd = new SqlCommand(@"
                SELECT TOP 1 * FROM InterviewSessions 
                WHERE UserId = @UserId
                ORDER BY CreatedAt DESC", conn);
            cmd.Parameters.AddWithValue("@UserId", userId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new InterviewSession
                {
                    Id = (int)reader["Id"],
                    UserId = (string)reader["UserId"],
                    JobTitle = (string)reader["JobTitle"],
                    QuestionIndex = (int)reader["QuestionIndex"],
                    Questions = JsonConvert.DeserializeObject<List<string>>((string)reader["Questions"]) ?? new(),
                    Answers = JsonConvert.DeserializeObject<List<string>>((string)reader["Answers"]) ?? new(),
                    IsComplete = (bool)reader["IsComplete"],
                    TabSwitchCount = (int)reader["TabSwitchCount"],
                    CreatedAt = (DateTime)reader["CreatedAt"]
                };
            }

            return null;
        }

        public void UpdateInterviewSession(InterviewSession session)
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            var cmd = new SqlCommand(@"
                UPDATE InterviewSessions 
                SET QuestionIndex = @QuestionIndex, 
                    Questions = @Questions, 
                    Answers = @Answers,
                    IsComplete = @IsComplete,
                    TabSwitchCount = @TabSwitchCount
                WHERE Id = @Id", conn);

            cmd.Parameters.AddWithValue("@Id", session.Id);
            cmd.Parameters.AddWithValue("@QuestionIndex", session.QuestionIndex);
            cmd.Parameters.AddWithValue("@Questions", JsonConvert.SerializeObject(session.Questions));
            cmd.Parameters.AddWithValue("@Answers", JsonConvert.SerializeObject(session.Answers));
            cmd.Parameters.AddWithValue("@IsComplete", session.IsComplete);
            cmd.Parameters.AddWithValue("@TabSwitchCount", session.TabSwitchCount);

            cmd.ExecuteNonQuery();
        }

        public void UpdateTabSwitchCount(string userId, int count)
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            var cmd = new SqlCommand(@"
                UPDATE InterviewSessions 
                SET TabSwitchCount = COALESCE(TabSwitchCount, 0) + @Count
                WHERE UserId = @UserId AND IsComplete = 0", conn);

            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@Count", count);
            cmd.ExecuteNonQuery();
        }

        public void SaveFullConversation(string userId, string name, string phone, string email, List<ChatMessage> messages)
        {
            try
            {
                if (messages == null || messages.Count == 0)
                    return;

                var sb = new System.Text.StringBuilder();
                var now = DateTime.Now;
                var sessionStartTimeKey = $"SessionStartTime_{userId}";
                var sessionFileNameKey = $"SessionFileName_{userId}";
                var folderPath = @"C:\Conversation";

                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                var sessionStartTimeStr = _httpContextAccessor.HttpContext?.Session.GetString(sessionStartTimeKey);
                DateTime sessionStartTime;
                bool isNewSession = string.IsNullOrEmpty(sessionStartTimeStr) ||
                                    !DateTime.TryParse(sessionStartTimeStr, out sessionStartTime) ||
                                    (now - sessionStartTime).TotalMinutes >= 30;

                string sessionFileName = null;
                if (!string.IsNullOrEmpty(name) && (!string.IsNullOrEmpty(phone) || !string.IsNullOrEmpty(email)))
                {
                    var identifier = !string.IsNullOrEmpty(phone) ? phone.Replace("+", "").Replace(" ", "") : email.Replace("@", "_").Replace(".", "_");
                    sessionFileName = $"{name.Replace(" ", "_")}_{identifier}.txt";
                }
                else
                {
                    sessionFileName = $"session_{userId}.txt";
                }

                var finalFilePath = Path.Combine(folderPath, sessionFileName);
                _httpContextAccessor.HttpContext?.Session.SetString(sessionFileNameKey, sessionFileName);

                if (isNewSession)
                {
                    sb.AppendLine($"=============================");
                    sb.AppendLine($"🕒 New Session on {now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"=============================");
                    sb.AppendLine();
                    _httpContextAccessor.HttpContext?.Session.SetString(sessionStartTimeKey, now.ToString("o"));
                }

                bool isInterviewComplete = messages.Any(m => m.BotResponse.Contains("Thank you for completing the interview"));

                foreach (var msg in messages)
                {
                    sb.AppendLine($"🕒 {msg.CreatedAt:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"👤 User: {msg.UserMessage}");
                    sb.AppendLine($"🤖 Bot : {msg.BotResponse}");
                    sb.AppendLine();
                }

                var session = GetLatestSession(userId);
                if (isInterviewComplete && session != null)
                {
                    sb.AppendLine($"🔄 Tab Switch Count: {session.TabSwitchCount}");
                }
                else if (isInterviewComplete && session == null)
                {
                    sb.AppendLine("🔄 No interview session found.");
                }

                string conversationText = sb.ToString();

                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                using var transaction = conn.BeginTransaction();
                try
                {
                    var cmd = new SqlCommand(@"
                        INSERT INTO ChatMessages (UserId, Name, Phone, Email, ConversationText, CreatedAt)
                        VALUES (@UserId, @Name, @Phone, @Email, @Text, @CreatedAt)", conn, transaction);

                    cmd.Parameters.AddWithValue("@UserId", userId ?? "");
                    cmd.Parameters.AddWithValue("@Name", name ?? "");
                    cmd.Parameters.AddWithValue("@Phone", phone ?? "");
                    cmd.Parameters.AddWithValue("@Email", email ?? "");
                    cmd.Parameters.AddWithValue("@Text", conversationText);
                    cmd.Parameters.AddWithValue("@CreatedAt", now);
                    cmd.ExecuteNonQuery();

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }

                lock (new object())
                {
                    File.AppendAllText(finalFilePath, conversationText);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving conversation for UserId: {UserId}", userId);
                File.WriteAllText(Path.Combine(@"C:\Conversation", "error.txt"), ex.ToString());
            }
        }
    }
}