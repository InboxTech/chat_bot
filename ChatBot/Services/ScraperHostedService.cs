using Microsoft.Extensions.Hosting;
using System.Text;

namespace ChatBot.Services
{
    public class ScraperHostedService : BackgroundService
    {
        public static string LatestWebsiteContent = "";

        // Hardcoded job positions (from inboxtechs.com/careers)
        private readonly string[] _hardcodedJobs = new[]
        {
            "SAP Consultant (Treasury & Fico)",
            "Business Development Manager (BDM)",
            "React.Js Developer",
            "Node.js Developer",
            "Business Development Executive (BDE)",
            "UI/UX Designer"
        };

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var sb = new StringBuilder();

                // 🧑‍💻 Make the hardcoded job list clearer for GPT/Gemini
                sb.AppendLine("=== JOB OPENINGS START ===");
                for (int i = 0; i < _hardcodedJobs.Length; i++)
                {
                    sb.AppendLine($"{i + 1}. {_hardcodedJobs[i]}");
                }
                sb.AppendLine("=== JOB OPENINGS END ===\n");

                // Optional: Still include snapshot of the /careers page for context
                sb.AppendLine("=== HTML CONTENT START ===");
                using var httpClient = new HttpClient();
                try
                {
                    var html = await httpClient.GetStringAsync("https://inboxtechs.com/careers");
                    sb.AppendLine(html);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[Failed to load careers page: {ex.Message}]");
                }
                sb.AppendLine("=== HTML CONTENT END ===");

                LatestWebsiteContent = sb.ToString();

                // Optional: Save snapshot for debugging
                Directory.CreateDirectory("wwwroot");
                File.WriteAllText("wwwroot/scraped_content.txt", LatestWebsiteContent);

                Console.WriteLine("✅ Job list injected and content saved.");
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }
    }
}
