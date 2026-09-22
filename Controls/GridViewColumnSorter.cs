using System.Windows;

namespace Seikan.Connect.DeveloperTool.Controls
{
    // Adapter for the sort-indicator contract used by the imported dnSpy header template.
    public enum GridViewSortDirection { None, Ascending, Descending }

    public static class GridViewColumnSorter
    {
        public static readonly DependencyProperty GridViewSortDirectionProperty =
            DependencyProperty.RegisterAttached("GridViewSortDirection", typeof(GridViewSortDirection),
                typeof(GridViewColumnSorter), new FrameworkPropertyMetadata(GridViewSortDirection.None));

        public static GridViewSortDirection GetGridViewSortDirection(DependencyObject element) =>
            (GridViewSortDirection)element.GetValue(GridViewSortDirectionProperty);

        public static void SetGridViewSortDirection(DependencyObject element, GridViewSortDirection value) =>
            element.SetValue(GridViewSortDirectionProperty, value);
    }
}
