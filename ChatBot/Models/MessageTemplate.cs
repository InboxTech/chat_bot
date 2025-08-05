namespace ChatBot.Models
{
    public class MessageTemplate
    {
        public int TemplateId { get; set; }
        public string TemplateName { get; set; }
        public string MessageType { get; set; } // "Email" or "SMS"
        public string TemplateContent { get; set; }
        public bool IsDefault { get; set; }
    }
}