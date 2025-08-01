namespace ChatBot.Models
{
    public class ChatMessage
    {
        public string UserId { get; set; }
        public string UserMessage { get; set; }
        public string BotResponse { get; set; }
        public string Model { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}