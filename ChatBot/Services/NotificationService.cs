using System.Net.Mail;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Twilio.Rest.Api.V2010.Account;
using Twilio;

namespace ChatBot.Services
{
    public class NotificationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(IConfiguration configuration, ILogger<NotificationService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task SendEmailAsync(string toEmail, string subject, string body)
        {
            try
            {
                var smtpClient = new SmtpClient(_configuration["Smtp:Host"])
                {
                    Port = int.Parse(_configuration["Smtp:Port"]),
                    Credentials = new NetworkCredential(_configuration["Smtp:Username"], _configuration["Smtp:Password"]),
                    EnableSsl = true
                };

                var mailMessage = new MailMessage
                {
                    From = new MailAddress(_configuration["Smtp:FromEmail"]),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = false
                };
                mailMessage.To.Add(toEmail);

                await smtpClient.SendMailAsync(mailMessage);
                _logger.LogInformation("Email sent to {ToEmail}", toEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending email to {ToEmail}", toEmail);
                throw;
            }
        }

        public async Task SendWhatsAppAsync(string toPhone, string body)
        {
            try
            {
                TwilioClient.Init(_configuration["Twilio:AccountSid"], _configuration["Twilio:AuthToken"]);
                var message = await MessageResource.CreateAsync(
                    body: body,
                    from: new Twilio.Types.PhoneNumber($"whatsapp:{_configuration["Twilio:FromPhone"]}"),
                    to: new Twilio.Types.PhoneNumber($"whatsapp:{toPhone}")
                );
                _logger.LogInformation("WhatsApp message sent to {ToPhone}, SID: {MessageSid}", toPhone, message.Sid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending WhatsApp message to {ToPhone}", toPhone);
                throw;
            }
        }
    }
}