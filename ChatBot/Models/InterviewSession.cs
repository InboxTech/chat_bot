using System;
using System.Collections.Generic;

namespace ChatBot.Models
{
    public class InterviewSession
    {
        public int Id { get; set; }

        public string UserId { get; set; } = "";
        public string JobTitle { get; set; } = "";
        public int QuestionIndex { get; set; } = 0;
        public List<string> Questions { get; set; } = new List<string>();
        public List<string> Answers { get; set; } = new List<string>();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public int TabSwitchCount { get; set; }

        public bool IsComplete { get; set; }
        public bool IsSubmitted { get; set; }
    }
}
