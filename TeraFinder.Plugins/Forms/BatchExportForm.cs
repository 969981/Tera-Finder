using PKHeX.Core;
using TeraFinder.Core;

namespace TeraFinder.Plugins;

/// <summary>
/// Shared export dialog for raid calculator and reward calculator.
/// Kept separate from the generated WinForms designer to avoid disturbing
/// existing control layout and localizations.
/// </summary>
internal sealed class BatchExportForm : Form
{
    private readonly BatchExportOptions _prototype;
    private readonly bool _zh;

    private readonly CheckBox _pk9 = new() { Text = "PK9 (.pk9)", Checked = true, AutoSize = true };
    private readonly CheckBox _bin = new() { Text = "BIN (.bin)", AutoSize = true };
    private readonly CheckBox _zip = new() { Checked = true, AutoSize = true };
    private readonly CheckBox _manifest = new() { Checked = true, AutoSize = true };
    private readonly CheckBox _stop = new() { AutoSize = true };
    private readonly ComboBox _ballMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ComboBox _fixedBall = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly CheckedListBox _ballPool = new()
    {
        CheckOnClick = true,
        MultiColumn = true,
        ColumnWidth = 135,
        IntegralHeight = false,
        Size = new Size(660, 125),
    };
    private readonly ProgressBar _progress = new() { Width = 665, Height = 20 };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Button _start = new() { Width = 145, Height = 34 };
    private readonly Button _cancel = new() { Width = 120, Height = 34 };

    private CancellationTokenSource? _cts;
    private bool _running;

    public BatchExportForm(BatchExportOptions prototype, string language)
    {
        _prototype = prototype;
        _zh = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        Text = T("批量导出宝可梦", "Batch Pokémon Export");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(728, 560);
        AutoScaleMode = AutoScaleMode.Font;

        var label = new Label
        {
            Text = T($"待导出：{prototype.Entries.Count} 条计算结果", $"Results to export: {prototype.Entries.Count}"),
            Location = new Point(20, 15), AutoSize = true,
        };
        Controls.Add(label);

        var format = new GroupBox
        {
            Text = T("输出格式（可以同时选择）", "Output formats (multiple allowed)"),
            Location = new Point(20, 44), Size = new Size(687, 65),
        };
        _pk9.Location = new Point(18, 27);
        _bin.Location = new Point(168, 27);
        format.Controls.AddRange([_pk9, _bin]);
        Controls.Add(format);

        var ball = new GroupBox
        {
            Text = T("精灵球策略", "Ball policy"),
            Location = new Point(20, 120), Size = new Size(687, 88),
        };
        _ballMode.Items.AddRange([
            T("保持原默认球种", "Original default"),
            T("指定球种", "Fixed ball"),
            T("随机合法球种", "Random legal ball"),
        ]);
        _ballMode.SelectedIndex = 2;
        _ballMode.Location = new Point(18, 30);
        _fixedBall.Location = new Point(250, 30);
        foreach (var value in Enum.GetValues<Ball>().Where(x => x >= Ball.Master && x <= Ball.Beast).Distinct())
            _fixedBall.Items.Add(value);
        _fixedBall.SelectedItem = Ball.Poke;
        _ballMode.SelectedIndexChanged += (_, _) => RefreshBallControls();
        ball.Controls.AddRange([_ballMode, _fixedBall]);
        Controls.Add(ball);

        var poolGroup = new GroupBox
        {
            Text = T("随机球种池（仅随机模式有效，全部不选表示不限）",
                "Allowed random-ball pool (unchecked all = unrestricted)"),
            Location = new Point(20, 215), Size = new Size(687, 170),
        };
        _ballPool.Location = new Point(12, 27);
        foreach (var value in Enum.GetValues<Ball>().Where(x => x >= Ball.Master && x <= Ball.Beast).Distinct())
            _ballPool.Items.Add(value);
        poolGroup.Controls.Add(_ballPool);
        Controls.Add(poolGroup);

        var output = new GroupBox
        {
            Text = T("输出选项", "Output options"),
            Location = new Point(20, 393), Size = new Size(687, 68),
        };
        _zip.Text = T("生成 ZIP", "Create ZIP");
        _manifest.Text = T("导出清单 CSV", "Manifest CSV");
        _stop.Text = T("失败时停止", "Stop on failure");
        _zip.Location = new Point(18, 28);
        _manifest.Location = new Point(172, 28);
        _stop.Location = new Point(385, 28);
        output.Controls.AddRange([_zip, _manifest, _stop]);
        Controls.Add(output);

        _progress.Location = new Point(21, 472);
        _progress.Maximum = Math.Max(1, prototype.Entries.Count);
        Controls.Add(_progress);
        _status.Location = new Point(21, 500);
        _status.Text = T("选择导出目录后开始执行。", "Choose a destination folder to start.");
        Controls.Add(_status);

        _start.Location = new Point(423, 516);
        _start.Text = T("开始导出...", "Export...");
        _start.Click += StartExport;
        Controls.Add(_start);

        _cancel.Location = new Point(580, 516);
        _cancel.Text = T("关闭", "Close");
        _cancel.Click += (_, _) =>
        {
            if (_running)
                _cts?.Cancel();
            else
                Close();
        };
        Controls.Add(_cancel);

        FormClosing += (_, e) =>
        {
            if (_running)
            {
                _cts?.Cancel();
                e.Cancel = true;
            }
        };
        RefreshBallControls();
    }

    private string T(string zh, string en) => _zh ? zh : en;

    private void RefreshBallControls()
    {
        _fixedBall.Enabled = _ballMode.SelectedIndex == 1;
        _ballPool.Enabled = _ballMode.SelectedIndex == 2;
    }

    private async void StartExport(object? sender, EventArgs e)
    {
        if (!_pk9.Checked && !_bin.Checked)
        {
            MessageBox.Show(T("请至少选择一种格式。", "Select at least one format."), Text);
            return;
        }

        using var folder = new FolderBrowserDialog
        {
            Description = T("选择批量导出文件保存位置", "Choose the output folder"),
            UseDescriptionForTitle = true,
        };
        if (folder.ShowDialog(this) != DialogResult.OK)
            return;

        var pool = _ballPool.CheckedItems.Cast<Ball>().ToHashSet();
        var options = _prototype with
        {
            DestinationDirectory = folder.SelectedPath,
            ExportPK9 = _pk9.Checked,
            ExportBIN = _bin.Checked,
            CreateZIP = _zip.Checked,
            WriteManifest = _manifest.Checked,
            StopOnError = _stop.Checked,
            BallMode = (BatchBallMode)_ballMode.SelectedIndex,
            FixedBall = _fixedBall.SelectedItem is Ball b ? b : Ball.Poke,
            AllowedBalls = pool.Count == 0 ? null : pool,
        };

        _running = true;
        _start.Enabled = false;
        _cancel.Text = T("取消任务", "Cancel task");
        _cts = new CancellationTokenSource();
        _status.Text = T("正在生成和验证...", "Generating and validating...");
        var progress = new Progress<BatchExportProgress>(p =>
        {
            _progress.Value = Math.Min(_progress.Maximum, p.Completed);
            _status.Text = T(
                $"已处理 {p.Completed}/{p.Total}，导出 {p.Exported}，跳过 {p.Skipped}",
                $"Processed {p.Completed}/{p.Total}, exported {p.Exported}, skipped {p.Skipped}");
        });

        try
        {
            var result = await Task.Run(() => BatchExportService.Export(options, progress, _cts.Token));
            _status.Text = T(
                $"完成：{result.Exported} 个结果，{result.WrittenFiles} 个文件，跳过 {result.Skipped}。",
                $"Done: {result.Exported} results, {result.WrittenFiles} files, skipped {result.Skipped}.");
            MessageBox.Show(this,
                T($"导出完成：{result.OutputPath}\n成功 {result.Exported}，跳过 {result.Skipped}。",
                    $"Export complete: {result.OutputPath}\nExported {result.Exported}, skipped {result.Skipped}."),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _status.Text = T("已取消，临时文件已清理。", "Cancelled; temporary files have been removed.");
        }
        catch (Exception ex)
        {
            _status.Text = T("导出失败。", "Export failed.");
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _running = false;
            _start.Enabled = true;
            _cancel.Text = T("关闭", "Close");
        }
    }
}
