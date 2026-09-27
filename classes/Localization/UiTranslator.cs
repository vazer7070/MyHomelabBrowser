using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MyHomelabBrowser.classes.Localization
{
    /// <summary>
    /// Traduit les textes posés dans le XAML au moment où chaque élément se charge
    /// (fenêtres, vues, menus, modèles). Seules les valeurs littérales sont remplacées :
    /// une liaison de données ou une valeur de style n'est jamais écrasée.
    /// </summary>
    public static class UiTranslator
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered || !Loc.IsTranslating)
                return;

            _registered = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(OnToolTipOpening), handledEventsToo: true);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
                Translate(element);
        }

        private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
        {
            // Info-bulle affectée après le chargement de l'élément.
            if (sender is FrameworkElement element)
                TranslateProperty(element, FrameworkElement.ToolTipProperty);
        }

        public static void Translate(FrameworkElement element)
        {
            TranslateProperty(element, FrameworkElement.ToolTipProperty);

            switch (element)
            {
                case TextBlock textBlock:
                    TranslateProperty(textBlock, TextBlock.TextProperty);
                    TranslateInlines(textBlock.Inlines);
                    break;

                case Window window:
                    TranslateProperty(window, Window.TitleProperty);
                    TranslateProperty(window, ContentControl.ContentProperty);
                    break;

                case MenuItem menuItem:
                    TranslateProperty(menuItem, HeaderedItemsControl.HeaderProperty);
                    TranslateProperty(menuItem, MenuItem.InputGestureTextProperty);
                    break;

                case HeaderedContentControl headered:
                    TranslateProperty(headered, HeaderedContentControl.HeaderProperty);
                    TranslateProperty(headered, ContentControl.ContentProperty);
                    break;

                case HeaderedItemsControl headeredItems:
                    TranslateProperty(headeredItems, HeaderedItemsControl.HeaderProperty);
                    break;

                case ContentControl content:
                    TranslateProperty(content, ContentControl.ContentProperty);
                    break;
            }

            // Les éléments d'une liste déroulante fermée ne sont pas encore chargés, mais la zone
            // de sélection affiche déjà le contenu de l'élément choisi : on les traduit ici.
            if (element is ComboBox combo)
            {
                bool changed = false;
                foreach (object item in combo.Items)
                {
                    if (item is ContentControl child && TranslateProperty(child, ContentControl.ContentProperty))
                        changed = true;
                }

                // La zone de sélection garde une copie du contenu : on la rafraîchit.
                if (changed && combo.SelectedIndex >= 0)
                {
                    int selected = combo.SelectedIndex;
                    combo.SelectedIndex = -1;
                    combo.SelectedIndex = selected;
                }
            }
        }

        private static void TranslateInlines(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case Run run when IsLiteral(run, Run.TextProperty) && Loc.TryTranslate(run.Text, out string translated):
                        run.Text = translated;
                        break;
                    case Span span:
                        TranslateInlines(span.Inlines);
                        break;
                }
            }
        }

        private static bool TranslateProperty(DependencyObject target, DependencyProperty property)
        {
            if (target.GetValue(property) is not string value || value.Length == 0)
                return false;

            if (!IsLiteral(target, property) || !Loc.TryTranslate(value, out string translated))
                return false;

            target.SetCurrentValue(property, translated);
            return true;
        }

        /// <summary>
        /// Valeur écrite telle quelle dans le XAML (ou le code), pas une liaison ni un style.
        /// </summary>
        private static bool IsLiteral(DependencyObject target, DependencyProperty property)
        {
            ValueSource source = DependencyPropertyHelper.GetValueSource(target, property);
            return !source.IsExpression &&
                   !source.IsAnimated &&
                   (source.BaseValueSource == BaseValueSource.Local ||
                    source.BaseValueSource == BaseValueSource.ParentTemplate);
        }
    }
}
