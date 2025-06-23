using Microsoft.Data.SqlClient;
using ChatBot.Models;
using Newtonsoft.Json;

namespace ChatBot.Services
{
    public class ChatDbService
    {
        private readonly string _connectionString;

        public ChatDbService(IConfiguration config)
        {
            _connectionString = config.GetConnectionString("DefaultConnection");
        }

        public void SaveMessage(ChatMessage message)
        {
            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                string sql = "INSERT INTO ChatMessages (UserMessage, BotResponse, CreatedAt) VALUES (@user, @bot, @time)";
                SqlCommand cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@user", message.UserMessage);
                cmd.Parameters.AddWithValue("@bot", message.BotResponse);
                cmd.Parameters.AddWithValue("@time", message.CreatedAt);

                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        // Interview session methods below 👇

        public void SaveInterviewSession(InterviewSession session)
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            var cmd = new SqlCommand(@"
                INSERT INTO InterviewSessions (UserId, JobTitle, QuestionIndex, Questions, Answers)
                VALUES (@UserId, @JobTitle, @QuestionIndex, @Questions, @Answers)", conn);

            cmd.Parameters.AddWithValue("@UserId", session.UserId);
            cmd.Parameters.AddWithValue("@JobTitle", session.JobTitle);
            cmd.Parameters.AddWithValue("@QuestionIndex", session.QuestionIndex);
            cmd.Parameters.AddWithValue("@Questions", JsonConvert.SerializeObject(session.Questions));
            cmd.Parameters.AddWithValue("@Answers", JsonConvert.SerializeObject(session.Answers));

            cmd.ExecuteNonQuery();
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
                    Answers = @Answers
                WHERE Id = @Id", conn);

            cmd.Parameters.AddWithValue("@Id", session.Id);
            cmd.Parameters.AddWithValue("@QuestionIndex", session.QuestionIndex);
            cmd.Parameters.AddWithValue("@Questions", JsonConvert.SerializeObject(session.Questions));
            cmd.Parameters.AddWithValue("@Answers", JsonConvert.SerializeObject(session.Answers));

            cmd.ExecuteNonQuery();
        }

        public void UpdateTabSwitchCount(string userId, int count)
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            var cmd = new SqlCommand(@"
        UPDATE InterviewSessions 
        SET TabSwitchCount = @Count
        WHERE UserId = @UserId AND QuestionIndex < (SELECT COUNT(*) FROM OPENJSON(Questions))
    ", conn);

            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@Count", count);
            cmd.ExecuteNonQuery();
        }

    }
}
