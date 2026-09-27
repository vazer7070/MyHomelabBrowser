using System;

namespace MyHomelabBrowser.classes
{
    public enum ToastKind
    {
        Info,
        Success,
        Warning
    }

    public class ToastItem
    {
        public string Title { get; init; } = "";
        public string Message { get; init; } = "";
        public ToastKind Kind { get; init; } = ToastKind.Info;

        // Action facultative affichée sous forme de bouton (ouvrir un fichier, redémarrer…).
        public string? ActionLabel { get; init; }
        public Action? Action { get; init; }

        public bool HasAction => Action != null && !string.IsNullOrWhiteSpace(ActionLabel);
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(5);
    }
}
