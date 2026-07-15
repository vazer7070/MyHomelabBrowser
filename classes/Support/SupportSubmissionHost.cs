using System;

namespace MyHomelabBrowser.classes.Support
{
    public static class SupportSubmissionHost
    {
        private static readonly Lazy<SupportSubmissionService> Instance =
            new(() => new SupportSubmissionService(), isThreadSafe: true);

        public static SupportSubmissionService Current => Instance.Value;
    }
}
