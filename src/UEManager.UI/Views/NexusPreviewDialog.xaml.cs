using System.Windows.Controls;

namespace UEManager.UI.Views;

public partial class NexusPreviewDialog : UserControl
{
    /// <summary>Each mod's preview starts at its top, not where the last one was scrolled to.</summary>
    public NexusPreviewDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Scroller.ScrollToTop();
    }
}
