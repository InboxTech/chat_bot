namespace ChatBot.Models
{
    public class PreInterviewState
    {
        public string? Name { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? ContactMethod { get; set; } // Tracks "phone" or "email"
        public string? SelectedJob { get; set; }
        public string? Experience { get; set; }
        public string? EmploymentStatus { get; set; }
        public string? Reason { get; set; }
        public int Step { get; set; }
    }
}
