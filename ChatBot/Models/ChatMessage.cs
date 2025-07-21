namespace ChatBot.Models
{
    public class ChatMessage
    {
        public int Id { get; set; }
        public string UserId { get; set; }  // Needed for identifying the session/user
        public string Name { get; set; }    // Used when storing full chat
        public string Phone { get; set; }   // Used when storing full chat
        public string UserMessage { get; set; }
        public string BotResponse { get; set; }
        public string Model { get; set; }
        public DateTime CreatedAt { get; set; }

        // Only used for full conversation logging (optional)
        public string ConversationText { get; set; }
    }
}