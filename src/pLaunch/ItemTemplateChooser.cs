using System.Windows;
using System.Windows.Controls;
using pLaunch.Models;
using pLaunch.ViewModels;

namespace pLaunch;

/// <summary>Picks the item template (PopupWindow.xaml resources) for the view mode and the item kind.</summary>
public sealed class ItemTemplateChooser(ResourceDictionary resources, ViewMode view) : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        var key = item is ItemViewModel { IsSeparator: true }
            ? view == ViewMode.List ? "SeparatorListTemplate" : "SeparatorTileTemplate"
            : view switch
            {
                ViewMode.Grid => "GridItemTemplate",
                ViewMode.Icons => "IconItemTemplate",
                _ => "ListItemTemplate",
            };
        return resources[key] as DataTemplate;
    }
}
