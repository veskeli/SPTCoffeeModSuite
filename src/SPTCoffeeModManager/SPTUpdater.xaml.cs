using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;

namespace SPTCoffeeModManager;

public partial class SptUpdater : Window
{
    private static readonly HttpClient UpdaterHttpClient = new() { Timeout = TimeSpan.FromMinutes(60) };


    public SptUpdater(string baseUrl, string basePath)
    {
        InitializeComponent();

        // Apply Windows 11 rounded corners if available
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _ = WindowCornerHelper.TrySetWindowCornerPreference(hwnd, WindowCornerHelper.DwmWindowCorner.Round);
        };

        Loaded += async (_, _) =>
        {
            // Start update async after small delay
            await Task.Delay(200); // allow UI to finish rendering
            try
            {
                await UpdateAsync(baseUrl, basePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update failed: {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
    }

    private async Task UpdateAsync(string baseUrl, string basePath)
    {
        var updateUrl = $"{baseUrl}/spt/update";
        var sptTempPath = Path.Combine(basePath, "spt_temp");
        Directory.CreateDirectory(sptTempPath);

        var success = await StartDownloadAsync(updateUrl, sptTempPath);

        if (!success)
        {
            ProgressText.Text = "Update failed.";
            ProgressText.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        var zipFile = Path.Combine(basePath, "spt_temp", "spt_update.zip");

        BackupStatusText.Visibility = Visibility.Visible;

        // Backup existing files
        await BackupFilesFromZip(zipFile, basePath);

        BackupStatusText.Text += " Done.";
        BackupStatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;

        // Small non-blocking delay to wait for file operations to complete
        await Task.Delay(100);

        ExtractionStatusText.Visibility = Visibility.Visible;

        // Extract update files
        await ExtractUpdateFiles(zipFile, basePath);

        ExtractionStatusText.Text += " Done.";
        ExtractionStatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;

        // Small non-blocking delay to wait for file operations to complete
        await Task.Delay(100);

        FinalizationStatusText.Visibility = Visibility.Visible;

        // Delete temp zip file
        if (File.Exists(zipFile))
            File.Delete(zipFile);

        FinalizationStatusText.Text += " Done.";
        FinalizationStatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;

        ProgressText.Text = "Update completed successfully. You may now close this window.";
        ProgressText.Foreground = System.Windows.Media.Brushes.LimeGreen;
    }

    private async Task<bool> StartDownloadAsync(string updateUrl, string tempFilePath)
    {
        try
        {
            using var resp = await UpdaterHttpClient.GetAsync(updateUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using var source = await resp.Content.ReadAsStreamAsync();
            var tempFile = Path.Combine(tempFilePath, "spt_update.zip");

            if (File.Exists(tempFile))
                File.Delete(tempFile);

            await using var dest = File.Create(tempFile);

            var buffer = new byte[81920];
            long downloaded = 0;
            int read;

            if (total <= 0)
            {
                Dispatcher.Invoke(() =>
                {
                    DownloadProgressBar.IsIndeterminate = true;
                    PercentageText.Text = "Downloading...";
                });
            }

            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read));
                downloaded += read;

                if (total > 0)
                {
                    var percent = (int)(downloaded * 100 / total);
                    Dispatcher.Invoke(() =>
                    {
                        DownloadProgressBar.IsIndeterminate = false;
                        DownloadProgressBar.Value = percent;
                        PercentageText.Text = $"{percent}%";
                    });
                }
            }

            Dispatcher.Invoke(() =>
            {
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressBar.Value = 100;
                PercentageText.Text = "Done";
            });

            return true;
        }
        catch
        {
            Dispatcher.Invoke(() =>
            {
                ProgressText.Text = "Download failed.";
                ProgressText.Foreground = System.Windows.Media.Brushes.Red;
            });
            return false;
        }
    }

    private async Task BackupFilesFromZip(string zipPath, string basePath)
    {
        string backupDir = Path.Combine(basePath, "spt_temp", "backup");
        Directory.CreateDirectory(backupDir);

        using ZipArchive zip = ZipFile.OpenRead(zipPath);

        foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name))) // ignore directories
        {
            string targetFile = Path.Combine(basePath, entry.FullName);
            string backupFile = Path.Combine(backupDir, entry.FullName);

            // Ensure folder exists inside backup
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);

            // Only back up if existing file present
            if (File.Exists(targetFile))
                File.Copy(targetFile, backupFile, overwrite: true);

            await Task.Yield();
        }
    }

    private async Task ExtractUpdateFiles(string zipPath, string basePath)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);

        foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
        {
            // Skip "winhttp.dll"
            if (entry.Name.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase))
                continue;

            string destination = Path.Combine(basePath, entry.FullName);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            entry.ExtractToFile(destination, overwrite: true);

            await Task.Yield();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}