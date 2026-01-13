namespace MyHomelabBrowser.classes.Flash
{
    public sealed class FlashRule
    {
        public string Domain { get; set; } = "";
        public FlashMode Mode { get; set; } = FlashMode.None;
        public bool Enabled { get; set; } = true;
    }
}
