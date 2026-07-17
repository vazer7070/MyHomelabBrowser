namespace MyHomelabBrowser.classes.Flash
{
    public sealed record RuffleFailureInfo(string Reason, RuffleStatus Status)
    {
        public override string ToString() => Reason;
    }
}
