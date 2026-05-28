namespace SPTServerManager;

public partial class IncomingFilePreviewLoadingWindow
{
    public IncomingFilePreviewLoadingWindow()
    {
        InitializeComponent();
    }

    public void UpdateProgress(string status, int current, int total)
    {
        StatusTextBlock.Text = status;

        if (total > 0)
        {
            LoadingProgressBar.IsIndeterminate = false;
            LoadingProgressBar.Minimum = 0;
            LoadingProgressBar.Maximum = total;
            LoadingProgressBar.Value = Math.Max(0, Math.Min(current, total));
            var percentage = (int)Math.Round(LoadingProgressBar.Value / total * 100.0);
            ProgressTextBlock.Text = $"{percentage}%";
        }
        else
        {
            LoadingProgressBar.IsIndeterminate = true;
            ProgressTextBlock.Text = string.Empty;
        }
    }
}



