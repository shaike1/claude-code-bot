using System.Text.Json;

public class ClaudeCredentials
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public long ExpiresAt { get; set; }
    public string[]? Scopes { get; set; }
    public string? SubscriptionType { get; set; }
}

public class ClaudeCredentialsWrapper
{
    public ClaudeCredentials? ClaudeAiOauth { get; set; }
}

class Program
{
    static async Task Main(string[] args)
    {
        try
        {
            var credentialsPath = "/root/.claude/.credentials.json";
            
            if (!File.Exists(credentialsPath))
            {
                Console.WriteLine("❌ Credentials file not found");
                return;
            }
            
            var credentialsJson = await File.ReadAllTextAsync(credentialsPath);
            Console.WriteLine($"📄 Raw JSON: {credentialsJson.Substring(0, Math.Min(100, credentialsJson.Length))}...");
            
            var credentialsWrapper = JsonSerializer.Deserialize<ClaudeCredentialsWrapper>(credentialsJson);
            var credentials = credentialsWrapper?.ClaudeAiOauth;
            
            if (credentials?.AccessToken == null)
            {
                Console.WriteLine("❌ No access token found in credentials");
                return;
            }
            
            Console.WriteLine("✅ Access token found");
            Console.WriteLine($"🔑 Token prefix: {credentials.AccessToken.Substring(0, 20)}...");
            
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(credentials.ExpiresAt).DateTime;
            Console.WriteLine($"⏰ Expires at: {expiresAt}");
            Console.WriteLine($"🔗 Scopes: {string.Join(", ", credentials.Scopes ?? [])}");
            Console.WriteLine($"📊 Subscription: {credentials.SubscriptionType}");
            
            if (expiresAt <= DateTime.UtcNow.AddMinutes(5))
            {
                Console.WriteLine("⚠️ Token is expired or will expire soon");
            }
            else
            {
                Console.WriteLine("✅ Token is valid");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
        }
    }
}