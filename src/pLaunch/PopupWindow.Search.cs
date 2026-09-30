using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using pLaunch.ViewModels;

namespace pLaunch;

// Type to search: the search box appears with the first character typed and filters the whole list
public partial class PopupWindow
{
    string _search = "";

    bool IsSearching => _search.Length > 0;

    /// <summary>
    /// A character typed while the list has the focus starts (or continues) a search. Digits 1-9 open the
    /// n-th item instead, as long as no search is running.
    /// </summary>
    void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0 || char.IsControl(e.Text[0]) || _items.Any(i => i.IsEditing))
            return;
        if (e.OriginalSource is TextBox || !IsFromThisWindow(e.OriginalSource))
            return; // the search box itself, a rename box, a context menu
        if (!IsSearching && e.Text[0] is >= '1' and <= '9')
            return; // handled as "open item n" on KeyDown
        if (!IsSearching && e.Text == " ")
            return; // space selects in the list
        SearchBox.Text += e.Text;
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    bool IsFromThisWindow(object source) =>
        source is not DependencyObject d
        || PresentationSource.FromDependencyObject(d) is not { } origin
        || origin == PresentationSource.FromVisual(this);

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        if (text == _search)
            return;
        _search = text;
        Refresh();
        // The best result is ready for Enter
        if (IsSearching && _items.Count > 0)
            List.SelectedIndex = 0;
    }

    void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ClearSearch();
                break;
            case Key.Down or Key.Up when _items.Count > 0:
                int next = List.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                List.SelectedIndex = Math.Clamp(next, 0, _items.Count - 1);
                List.ScrollIntoView(List.SelectedItem);
                break;
            case Key.Enter when List.SelectedItem is ItemViewModel selected:
                Open(selected, asAdmin: Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
                break;
            case Key.Back when SearchBox.Text.Length == 0:
                ClearSearch();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void ClearSearch_Click(object sender, RoutedEventArgs e) => ClearSearch();

    /// <summary>Back to the level that was shown before searching.</summary>
    void ClearSearch()
    {
        bool was = IsSearching;
        ClearSearchText();
        SearchBar.Visibility = Visibility.Collapsed;
        if (was)
            Refresh();
        List.Focus();
    }

    /// <summary>Empties the search without refreshing (the caller refreshes).</summary>
    void ClearSearchText()
    {
        _search = "";
        if (SearchBox.Text.Length > 0)
            SearchBox.Text = ""; // TextChanged sees _search already empty: no refresh here
        SearchBar.Visibility = Visibility.Collapsed;
    }
}
