using System;
using Avalonia.Markup.Xaml;
using MyHomelabBrowser.classes.Localization;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Texte traduit dans le XAML : {l:Tr 'Nouvel onglet'}. La clé est le texte français,
    /// comme dans le reste du navigateur (lang/en.json).
    /// </summary>
    public sealed class TrExtension : MarkupExtension
    {
        public TrExtension()
        {
        }

        public TrExtension(string text)
        {
            Text = text;
        }

        public string Text { get; set; } = string.Empty;

        public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Tr(Text);
    }
}
