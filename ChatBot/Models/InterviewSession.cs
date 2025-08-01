namespace ChatBot.Models
{
    public class InterviewSession
    {
        public int Id { get; set; }
        public string UserId { get; set; }
        public string JobTitle { get; set; }
        public int QuestionIndex { get; set; }
        public List<string> Questions { get; set; } = new List<string>();
        public List<string> Answers { get; set; } = new List<string>();
        public bool IsComplete { get; set; }
        public bool IsSubmitted { get; set; }
        public int TabSwitchCount { get; set; }
        public string VideoPath { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}