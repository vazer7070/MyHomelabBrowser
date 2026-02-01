namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    public class CredentialEntry
    {
        public string Host { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = ""; 
        public string? FormAction { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public bool AlwaysSave { get; set; }
        public bool NeverSave { get; set; }

    }
}
