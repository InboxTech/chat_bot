using Microsoft.Data.SqlClient;
using ChatBot.Models;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace ChatBot.Services
{
    public class ChatDbService
    {
        private readonly string _connectionString;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public ChatDbService(IConfiguration config, IHttpContextAccessor httpContextAccessor)
        {
            _connectionString = config.GetConnectionString("DefaultConnection");
            _httpContextAccessor = httpContextAccessor;
        }

        public void SaveUserDetails(UserDetails user)
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            var cmd = new SqlCommand(@"
                INSERT INTO UserDetails (UserId, Name, Phone, Email, Experience, EmploymentStatus, Reason, CreatedAt)
                VALUES (@UserId, @Name, @Phone, @Email, @Experience, @EmploymentStatus, @Reason, @CreatedAt)", conn);

            cmd.Parameters.AddWithValue("@UserId", user.UserId ?? "");
            cmd.Parameters.AddWithValue("@Name", user.Name ?? "");
            cmd.Parameters.AddWithValue("@Phone", user.Phone ?? "");
            cmd.Parameters.AddWithValue("@Email", user.Email ?? "");
            cmd.Parameters.AddWithValue("@Experience", user.Experience ?? "");
            cmd.Parameters.AddWithValue("@EmploymentStatus", user.EmploymentStatus ?? "");
            cmd.Parameters.AddWithValue("@Reason", user.Reason ?? "");
            cmd.Parameters.AddWithValue("@CreatedAt", user.CreatedAt);

            cmd.ExecuteNonQuery();
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

        //public void SaveFullConversation(string userId, string name, string phone, string email, List<ChatMessage> messages)
        //{
        //    try
        //    {
        //        if (messages == null || messages.Count == 0)
        //            return;

        //        var sb = new System.Text.StringBuilder();
        //        var now = DateTime.Now;
        //        var sessionStartTimeKey = $"SessionStartTime_{userId}";
        //        var sessionFileNameKey = $"SessionFileName_{userId}";
        //        var folderPath = @"C:\Conversation";

        //        // Check for new session (30-minute timeout)
        //        var sessionStartTimeStr = _httpContextAccessor.HttpContext?.Session.GetString(sessionStartTimeKey);
        //        DateTime sessionStartTime;
        //        bool isNewSession = string.IsNullOrEmpty(sessionStartTimeStr) ||
        //                            !DateTime.TryParse(sessionStartTimeStr, out sessionStartTime) ||
        //                            (now - sessionStartTime).TotalMinutes >= 30;

        //        // Determine file name
        //        var sessionFileName = _httpContextAccessor.HttpContext?.Session.GetString(sessionFileNameKey);
        //        var defaultFileName = $"session_{userId}.txt";
        //        var userFileName = string.Empty;

        //        // If name and either phone or email are provided, use user-based file name
        //        if (!string.IsNullOrEmpty(name) && (!string.IsNullOrEmpty(phone) || !string.IsNullOrEmpty(email)))
        //        {
        //            var identifier = !string.IsNullOrEmpty(phone) ? phone.Replace("+", "").Replace(" ", "") : email.Replace("@", "_").Replace(".", "_");
        //            userFileName = $"{name}_{identifier}.txt";
        //        }

        //        // If no session file name is set, use default or user-based file name
        //        if (string.IsNullOrEmpty(sessionFileName))
        //        {
        //            sessionFileName = string.IsNullOrEmpty(userFileName) ? defaultFileName : userFileName;
        //            _httpContextAccessor.HttpContext?.Session.SetString(sessionFileNameKey, sessionFileName);
        //        }

        //        // If user details are provided and current session file is default, migrate content
        //        if (!string.IsNullOrEmpty(userFileName) && sessionFileName == defaultFileName && userFileName != defaultFileName)
        //        {
        //            var defaultFilePath = Path.Combine(folderPath, defaultFileName);
        //            if (File.Exists(defaultFilePath))
        //            {
        //                var existingContent = File.ReadAllText(defaultFilePath);
        //                sb.Append(existingContent);
        //                if (isNewSession)
        //                {
        //                    sb.AppendLine($"=============================");
        //                    sb.AppendLine($"🕒 Session on {now:yyyy-MM-dd HH:mm:ss}");
        //                    sb.AppendLine($"=============================");
        //                    sb.AppendLine();
        //                }
        //                sessionFileName = userFileName;
        //                _httpContextAccessor.HttpContext?.Session.SetString(sessionFileNameKey, sessionFileName);
        //            }
        //        }

        //        if (isNewSession && (string.IsNullOrEmpty(sessionStartTimeStr) || sb.Length == 0))
        //        {
        //            sb.AppendLine($"=============================");
        //            sb.AppendLine($"🕒 Session on {now:yyyy-MM-dd HH:mm:ss}");
        //            sb.AppendLine($"=============================");
        //            sb.AppendLine();
        //            _httpContextAccessor.HttpContext?.Session.SetString(sessionStartTimeKey, now.ToString("o"));
        //        }

        //        // Flag to check if interview is complete
        //        bool isInterviewComplete = messages.Any(m => m.BotResponse.Contains("Thank you for completing the interview"));

        //        foreach (var msg in messages)
        //        {
        //            sb.AppendLine($"🕒 {msg.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        //            sb.AppendLine($"👤 User: {msg.UserMessage}");
        //            sb.AppendLine($"🤖 Bot : {msg.BotResponse}");
        //            sb.AppendLine();
        //        }

        //        // Append tab switch count only after interview completion
        //        var session = GetLatestSession(userId);
        //        if (isInterviewComplete && session != null)
        //        {
        //            Console.WriteLine($"Fetched TabSwitchCount: {session.TabSwitchCount} for UserId: {userId}");
        //            sb.AppendLine($"🔄 Tab Switch Count: {session.TabSwitchCount}");
        //        }
        //        else if (session == null)
        //        {
        //            sb.AppendLine("🔄 No interview session found.");
        //        }

        //        string conversationText = sb.ToString();

        //        // Save to database
        //        using var conn = new SqlConnection(_connectionString);
        //        conn.Open();
        //        using var transaction = conn.BeginTransaction();
        //        try
        //        {
        //            var cmd = new SqlCommand(@"
        //                INSERT INTO ChatMessages (UserId, Name, Phone, Email, ConversationText, CreatedAt)
        //                VALUES (@UserId, @Name, @Phone, @Email, @Text, @CreatedAt)", conn, transaction);

        //            cmd.Parameters.AddWithValue("@UserId", userId ?? "");
        //            cmd.Parameters.AddWithValue("@Name", name ?? "");
        //            cmd.Parameters.AddWithValue("@Phone", phone ?? "");
        //            cmd.Parameters.AddWithValue("@Email", email ?? "");
        //            cmd.Parameters.AddWithValue("@Text", conversationText);
        //            cmd.Parameters.AddWithValue("@CreatedAt", now);
        //            cmd.ExecuteNonQuery();

        //            transaction.Commit();
        //        }
        //        catch
        //        {
        //            transaction.Rollback();
        //            throw;
        //        }

        //        // Save to file
        //        if (!Directory.Exists(folderPath))
        //            Directory.CreateDirectory(folderPath);

        //        var filePath = Path.Combine(folderPath, sessionFileName);
        //        lock (new object())
        //        {
        //            File.AppendAllText(filePath, conversationText);
        //            // If we switched to user-based file name, delete the default file
        //            if (!string.IsNullOrEmpty(userFileName) && sessionFileName == userFileName && File.Exists(Path.Combine(folderPath, defaultFileName)))
        //            {
        //                File.Delete(Path.Combine(folderPath, defaultFileName));
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.WriteLine("❌ SaveFullConversation error: " + ex.Message);
        //        File.WriteAllText(Path.Combine(@"C:\Conversation", "error.txt"), ex.ToString());
        //    }
        //}

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

                // Ensure folder exists
                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                // Check for new session (30-minute timeout)
                var sessionStartTimeStr = _httpContextAccessor.HttpContext?.Session.GetString(sessionStartTimeKey);
                DateTime sessionStartTime;
                bool isNewSession = string.IsNullOrEmpty(sessionStartTimeStr) ||
                                    !DateTime.TryParse(sessionStartTimeStr, out sessionStartTime) ||
                                    (now - sessionStartTime).TotalMinutes >= 30;

                // Determine base file names
                var defaultFileName = $"session_{userId}.txt";
                string userFileName = null;
                if (!string.IsNullOrEmpty(name) && (!string.IsNullOrEmpty(phone) || !string.IsNullOrEmpty(email)))
                {
                    var identifier = !string.IsNullOrEmpty(phone) ? phone.Replace("+", "").Replace(" ", "") : email.Replace("@", "_").Replace(".", "_");
                    userFileName = $"{name}_{identifier}.txt";
                }

                var sessionFileName = _httpContextAccessor.HttpContext?.Session.GetString(sessionFileNameKey);
                if (string.IsNullOrEmpty(sessionFileName))
                {
                    sessionFileName = string.IsNullOrEmpty(userFileName) ? defaultFileName : userFileName;
                    _httpContextAccessor.HttpContext?.Session.SetString(sessionFileNameKey, sessionFileName);
                }

                var finalFilePath = Path.Combine(folderPath, sessionFileName);

                // Migrate default content to user-based file if needed
                if (!string.IsNullOrEmpty(userFileName) && sessionFileName == defaultFileName && userFileName != defaultFileName)
                {
                    var defaultFilePath = Path.Combine(folderPath, defaultFileName);
                    if (File.Exists(defaultFilePath))
                    {
                        sb.Append(File.ReadAllText(defaultFilePath));
                        File.Delete(defaultFilePath);
                    }

                    sessionFileName = userFileName;
                    _httpContextAccessor.HttpContext?.Session.SetString(sessionFileNameKey, sessionFileName);
                    finalFilePath = Path.Combine(folderPath, sessionFileName);
                }

                // Write session header if needed
                if (isNewSession && (string.IsNullOrEmpty(sessionStartTimeStr) || sb.Length == 0))
                {
                    sb.AppendLine($"=============================");
                    sb.AppendLine($"🕒 Session on {now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"=============================");
                    sb.AppendLine();
                    _httpContextAccessor.HttpContext?.Session.SetString(sessionStartTimeKey, now.ToString("o"));
                }

                // Check if interview complete
                bool isInterviewComplete = messages.Any(m => m.BotResponse.Contains("Thank you for completing the interview"));

                foreach (var msg in messages)
                {
                    sb.AppendLine($"🕒 {msg.CreatedAt:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"👤 User: {msg.UserMessage}");
                    sb.AppendLine($"🤖 Bot : {msg.BotResponse}");
                    sb.AppendLine();
                }

                //var session = GetLatestSession(userId);
                //if (isInterviewComplete && session != null)
                //{
                //    sb.AppendLine($"🔄 Tab Switch Count: {session.TabSwitchCount}");
                //}
                //else if (session == null)
                //{
                //    sb.AppendLine("🔄 No interview session found.");
                //}

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

                // Save to database
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

                // Save to file
                lock (new object())
                {
                    File.AppendAllText(finalFilePath, conversationText);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ SaveFullConversation error: " + ex.Message);
                File.WriteAllText(Path.Combine(@"C:\Conversation", "error.txt"), ex.ToString());
            }
        }

    }
}