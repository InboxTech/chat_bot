namespace ChatBot.Models
{
    public class UserDetails
    {
        public string UserId { get; set; }
        public string Name { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string EmploymentStatus { get; set; }
        public string Experience { get; set; }
        public string Reason { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string IDProofPath { get; set; }
        public string IDProofType { get; set; } // New: e.g., Passport, Driver's License
        public DateTime CreatedAt { get; set; }
    }
}