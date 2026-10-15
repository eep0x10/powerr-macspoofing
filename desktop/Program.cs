using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace PowerRDesktop;

internal static class Program
{
    internal static bool TestMode { get; private set; }
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        TestMode = args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal);
        if (args.Length == 1 && args[0] == "--smoke")
        {
            using var window = new MainWindow();
            Task.Run(window.SmokeAsync).GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 2 && (args[0] == "--snapshot" || args[0] == "--snapshot-compact"))
        {
            using var window = new MainWindow();
            if (args[0] == "--snapshot-compact") window.Size = new Size(430, 800);
            window.Show();
            var until = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(50); }
            using var bitmap = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
            bitmap.Save(args[1]);
            static IEnumerable<string> Bounds(Control parent, string prefix) {
                foreach (Control child in parent.Controls) { yield return $"{prefix}{child.GetType().Name} {child.Text[..Math.Min(25, child.Text.Length)].Replace(Environment.NewLine, " ")} {child.Bounds}"; foreach (var nested in Bounds(child, prefix + "  ")) yield return nested; }
            }
            File.WriteAllLines(args[1] + ".layout.txt", Bounds(window, ""));
            return;
        }
        Application.Run(new MainWindow());
    }
}

internal sealed class MainWindow : Form
{
    private readonly Color canvas = Color.FromArgb(13, 25, 42);
    private readonly Color panel = Color.FromArgb(21, 38, 58);
    private readonly Color ink = Color.FromArgb(225, 237, 250);
    private readonly Color muted = Color.FromArgb(159, 183, 207);
    private readonly Color accent = Color.FromArgb(23, 188, 202);
    private readonly ComboBox adapters = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 350 };
    private readonly NumericUpDown seconds = new() { Minimum = 5, Maximum = 120, Value = 20, Width = 75 };
    private readonly TextBox macInput = new() { Width = 220, PlaceholderText = "02:11:22:33:44:55" };
    private readonly DataGridView results = new();
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Label state = new() { AutoSize = true };
    private readonly Label selectorLabel = MakeLabel("Adaptador", 10, Color.FromArgb(159, 183, 207));
    private readonly Label secondsLabel = MakeLabel("segundos", 10, Color.FromArgb(159, 183, 207));
    private readonly Label backupState = new() { AutoSize = true };
    private readonly Label hint = new() { AutoSize = true };
    private readonly Label emptyMessage = new() { Text = "Nenhum MAC observado nesta sessão.\nConecte o adaptador e inicie uma captura.", Size = new Size(360, 60), TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label captureHelp = MakeLabel("Selecione um MAC para preencher o plano. Confirme a origem pela topologia e pelos registros do switch antes de aplicar.", 10, Color.FromArgb(159, 183, 207));
    private readonly Label heading = MakeLabel("PowerR   |   captura e aplicação de MAC", 19, Color.FromArgb(225, 237, 250));
    private readonly ProgressBar progress = new() { Width = 180, Height = 16 };
    private readonly Button capture = new() { Text = "Iniciar captura", Width = 145 };
    private readonly Button apply = new() { Text = "Aplicar MAC", Width = 140 };
    private readonly Button restore = new() { Text = "Restaurar", Width = 140 };
    private Button? refreshButton;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TableLayoutPanel outer = new() { Dock = DockStyle.Fill, ColumnCount = 2 };
    private readonly FlowLayoutPanel navigation = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10, 22, 5, 5) };
    private readonly List<Button> navButtons = new();
    private TableLayoutPanel? shellLayout;
    private TableLayoutPanel? topLayout;
    private TableLayoutPanel? captureLayout;
    private readonly System.Windows.Forms.Timer captureTimer = new() { Interval = 1000 };
    private DateTime captureStarted;
    private int captureDuration;
    private bool busy;
    private string? activeGuid;
    private string? activeName;
    private string? activeStatus;
    private string? activeMac;
    private string? scriptPath;

    public MainWindow()
    {
        Text = "PowerR — operação de MAC";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(430, 620);
        Size = new Size(1100, 770);
        BackColor = canvas;
        ForeColor = ink;
        Font = new Font("Segoe UI", 10);
        BuildLayout();
        captureTimer.Tick += (_, _) => UpdateProgress();
        Shown += async (_, _) => await RefreshAdapters();
    }

    private void BuildLayout()
    {
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(outer);
        navigation.BackColor = Color.FromArgb(10, 31, 50);
        foreach (var (title, index) in new[] { ("Capturar", 0), ("Aplicar MAC", 1), ("Diagnóstico", 2), ("Restaurar", 3) })
        {
            var button = Button(title, (_, _) => tabs.SelectedIndex = index);
            button.Width = 145;
            button.Height = 44;
            button.Margin = new Padding(2, 6, 2, 6);
            navigation.Controls.Add(button);
            navButtons.Add(button);
        }
        outer.Controls.Add(navigation, 0, 0);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(16), BackColor = canvas };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shellLayout = shell;
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 125));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        outer.Controls.Add(shell, 1, 0);

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topLayout = top;
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        top.Controls.Add(heading, 0, 0);
        var selector = Flow();
        selector.Controls.Add(selectorLabel);
        selector.Controls.Add(adapters);
        var refresh = Button("Atualizar", async (_, _) => await RefreshAdapters());
        refreshButton = refresh;
        selector.Controls.Add(refresh);
        selector.Controls.Add(state);
        adapters.SelectedIndexChanged += async (_, _) => await SelectAdapter();
        top.Controls.Add(selector, 0, 1);
        shell.Controls.Add(top, 0, 0);

        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.ItemSize = new Size(142, 32);
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.DrawItem += (_, e) => {
            var selected = e.Index == tabs.SelectedIndex;
            using var brush = new SolidBrush(selected ? accent : panel);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, Font, e.Bounds, selected ? canvas : ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        tabs.TabPages.Add(BuildCapture());
        tabs.TabPages.Add(BuildApply());
        tabs.TabPages.Add(BuildDiagnose());
        tabs.TabPages.Add(BuildRestore());
        tabs.SelectedIndexChanged += (_, _) => UpdateNavigation();
        shell.Controls.Add(tabs, 0, 1);

        var bottom = Flow();
        backupState.Text = "Verificando estado de recuperação…";
        backupState.ForeColor = muted;
        bottom.Controls.Add(backupState);
        shell.Controls.Add(bottom, 0, 2);
        SizeChanged += (_, _) => UpdateNavigation();
        UpdateNavigation();
        SetBusy(false);
    }

    private void UpdateNavigation()
    {
        var compact = ClientSize.Width < 850;
        outer.ColumnStyles[0].Width = compact ? 0 : 170;
        navigation.Visible = !compact;
        tabs.ItemSize = compact ? new Size(80, 32) : new Size(1, 1);
        if (tabs.TabPages.Count == 4) { tabs.TabPages[1].Text = compact ? "Aplicar" : "Aplicar MAC"; tabs.TabPages[2].Text = compact ? "Diag." : "Diagnóstico"; }
        adapters.Width = compact ? 180 : 350;
        selectorLabel.Visible = !compact;
        state.Visible = !compact;
        secondsLabel.Visible = !compact;
        if (refreshButton is not null) { refreshButton.Text = compact ? "↻" : "Atualizar"; refreshButton.Width = compact ? 42 : 110; refreshButton.MinimumSize = new Size(compact ? 42 : 110, 35); }
        capture.Width = compact ? 112 : 145;
        progress.Visible = !compact || busy;
        heading.Text = compact ? "PowerR" : "PowerR   |   captura e aplicação de MAC";
        if (shellLayout is not null) shellLayout.RowStyles[0].Height = compact ? 168 : 125;
        if (topLayout is not null) topLayout.RowStyles[1].Height = compact ? 106 : 62;
        if (captureLayout is not null) captureLayout.RowStyles[0].Height = compact ? 128 : 86;
        if (results.Columns.Count == 4)
        {
            results.Columns[2].Visible = !compact;
            results.Columns[3].Visible = !compact;
            results.Columns[0].FillWeight = compact ? 75 : 25;
            results.Columns[1].FillWeight = compact ? 25 : 12;
            results.Columns[2].FillWeight = 35;
            results.Columns[3].FillWeight = 25;
        }
        emptyMessage.Width = compact ? 250 : 360;
        emptyMessage.Left = Math.Max(10, (results.ClientSize.Width - emptyMessage.Width) / 2);
        captureHelp.Text = compact ? "Selecione um MAC e confirme sua origem no switch." : "Selecione um MAC para preencher o plano. Confirme a origem pela topologia e pelos registros do switch antes de aplicar.";
        if (compact && activeStatus != "Up") hint.Text = "Conecte o adaptador para capturar.";
        foreach (var (button, index) in navButtons.Select((button, index) => (button, index)))
            button.BackColor = tabs.SelectedIndex == index ? Color.FromArgb(20, 125, 145) : Color.FromArgb(31, 75, 101);
        outer.PerformLayout();
    }

    private TabPage BuildCapture()
    {
        var page = Page("Capturar");
        var layout = Stack(page);
        captureLayout = layout;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        var controls = Flow();
        controls.Controls.Add(MakeLabel("Duração", 10, muted));
        controls.Controls.Add(seconds);
        controls.Controls.Add(secondsLabel);
        capture.Click += async (_, _) => await StartCapture();
        controls.Controls.Add(capture);
        controls.Controls.Add(progress);
        controls.Controls.Add(hint);
        layout.Controls.Add(controls, 0, 0);
        results.Dock = DockStyle.Fill;
        results.ReadOnly = true;
        results.AllowUserToAddRows = false;
        results.AllowUserToDeleteRows = false;
        results.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        results.MultiSelect = false;
        results.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        results.BackgroundColor = panel;
        results.BorderStyle = BorderStyle.None;
        results.GridColor = Color.FromArgb(51, 75, 98);
        results.EnableHeadersVisualStyles = false;
        results.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(29, 51, 73);
        results.ColumnHeadersDefaultCellStyle.ForeColor = ink;
        results.DefaultCellStyle.BackColor = panel;
        results.DefaultCellStyle.ForeColor = ink;
        results.DefaultCellStyle.SelectionBackColor = Color.FromArgb(25, 105, 130);
        results.DefaultCellStyle.SelectionForeColor = Color.White;
        results.RowHeadersVisible = false;
        results.Columns.Add("Mac", "MAC observado");
        results.Columns.Add("Packets", "Pacotes");
        results.Columns.Add("Protocols", "Protocolos");
        results.Columns.Add("FirstSeen", "Primeira vez");
        emptyMessage.ForeColor = muted;
        emptyMessage.BackColor = panel;
        results.Controls.Add(emptyMessage);
        void CenterEmpty() { emptyMessage.Left = Math.Max(10, (results.ClientSize.Width - emptyMessage.Width) / 2); emptyMessage.Top = Math.Max(70, (results.ClientSize.Height - emptyMessage.Height) / 2); }
        results.Resize += (_, _) => CenterEmpty();
        CenterEmpty();
        results.SelectionChanged += (_, _) => {
            if (results.SelectedRows.Count > 0) macInput.Text = results.SelectedRows[0].Cells[0].Value?.ToString() ?? "";
        };
        layout.Controls.Add(results, 0, 1);
        layout.Controls.Add(captureHelp, 0, 2);
        return page;
    }

    private TabPage BuildApply()
    {
        var page = Page("Aplicar MAC");
        var layout = Stack(page);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var input = Flow();
        input.Controls.Add(MakeLabel("MAC pretendido", 10, muted));
        input.Controls.Add(macInput);
        input.Controls.Add(Button("Revisar plano", async (_, _) => await Plan()));
        layout.Controls.Add(input, 0, 0);
        apply.Click += async (_, _) => await Apply();
        var action = Flow();
        action.Controls.Add(apply);
        action.Controls.Add(MakeLabel("A conexão será interrompida durante a aplicação.", 10, muted));
        layout.Controls.Add(action, 0, 1);
        layout.Controls.Add(details, 0, 2);
        return page;
    }

    private TabPage BuildDiagnose()
    {
        var page = Page("Diagnóstico");
        var layout = Stack(page);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Button("Atualizar diagnóstico", async (_, _) => await Diagnose()), 0, 0);
        var output = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        layout.Controls.Add(output, 0, 1);
        diagnoseOutput = output;
        return page;
    }
    private TextBox? diagnoseOutput;

    private TabPage BuildRestore()
    {
        var page = Page("Restaurar");
        var layout = Stack(page);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        restore.Click += async (_, _) => await Restore();
        layout.Controls.Add(restore, 0, 0);
        layout.Controls.Add(MakeLabel("A restauração usa o backup local deste adaptador. Ele é removido somente após a verificação do MAC original.", 11, muted), 0, 1);
        return page;
    }

    private static TabPage Page(string name) => new(name) { BackColor = Color.FromArgb(21, 38, 58), ForeColor = Color.White, Padding = new Padding(18) };
    private static TableLayoutPanel Stack(Control parent) { var t = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 }; t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); parent.Controls.Add(t); return t; }
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = true, Padding = new Padding(4, 10, 4, 4) };
    private static Label MakeLabel(string text, int size, Color color) => new() { Text = text, Font = new Font("Segoe UI", size), ForeColor = color, AutoSize = true, Margin = new Padding(6, 8, 12, 4) };
    private Button Button(string text, EventHandler handler) { var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(110, 35), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(31, 75, 101), ForeColor = Color.White }; b.FlatAppearance.BorderColor = accent; b.Click += handler; return b; }

    private string SelectedGuid() => activeGuid ?? throw new InvalidOperationException("Selecione um adaptador.");

    private async Task RefreshAdapters()
    {
        await Busy(async () => {
            var json = await Run("Adapters");
            var previous = activeGuid;
            adapters.Items.Clear();
            foreach (var item in json.RootElement.GetProperty("Items").EnumerateArray()) adapters.Items.Add(new AdapterItem(item));
            var index = Enumerable.Range(0, adapters.Items.Count).FirstOrDefault(i => ((AdapterItem)adapters.Items[i]!).Guid == previous, -1);
            if (index < 0) index = Enumerable.Range(0, adapters.Items.Count).FirstOrDefault(i => ((AdapterItem)adapters.Items[i]!).IsPhysicalEthernet, -1);
            if (adapters.Items.Count > 0) adapters.SelectedIndex = index >= 0 ? index : 0;
            return true;
        });
    }

    private async Task SelectAdapter()
    {
        if (adapters.SelectedItem is not AdapterItem item) return;
        activeGuid = item.Guid;
        activeName = item.Name;
        activeStatus = item.Status;
        activeMac = item.Mac;
        state.Text = $"{item.Status}  •  {item.Mac}";
        state.ForeColor = item.Status.Equals("Up", StringComparison.OrdinalIgnoreCase) ? accent : Color.Orange;
        capture.Enabled = !busy && item.Status.Equals("Up", StringComparison.OrdinalIgnoreCase);
        hint.Text = capture.Enabled ? "Pronto para capturar por tempo limitado." : ClientSize.Width < 850 ? "Conecte o adaptador para capturar." : "Adaptador desconectado. Conecte para iniciar a captura.";
        results.Rows.Clear();
        emptyMessage.Visible = true;
        details.Clear();
        await RefreshStatus();
    }

    private async Task RefreshStatus()
    {
        if (activeGuid is null) return;
        try {
            var status = await Run("Status", "-Adapter", activeGuid);
            var backup = Get(status.RootElement, "Backup");
            backupState.Text = string.IsNullOrWhiteSpace(backup)
                ? ClientSize.Width < 850 ? "Sem alteração ativa" : "Sem alteração ativa · Nenhum backup de restauração"
                : ClientSize.Width < 850 ? "Backup disponível" : "Sessão ativa · Backup disponível para restauração";
            backupState.ForeColor = string.IsNullOrWhiteSpace(backup) ? muted : Color.Orange;
            restore.Enabled = !busy && !string.IsNullOrWhiteSpace(backup);
        } catch (Exception ex) { backupState.Text = "Estado indisponível: " + ex.Message; restore.Enabled = false; }
    }

    private async Task StartCapture()
    {
        var guid = SelectedGuid();
        captureDuration = (int)seconds.Value;
        captureStarted = DateTime.UtcNow;
        progress.Minimum = 0; progress.Maximum = captureDuration; progress.Value = 0;
        captureTimer.Start();
        hint.Text = "Captura em andamento…";
        await Busy(async () => {
            var response = await Run("Capture", "-Adapter", guid, "-Seconds", captureDuration.ToString());
            if (guid != activeGuid) return true;
            results.Rows.Clear();
            foreach (var item in response.RootElement.GetProperty("Items").EnumerateArray())
                results.Rows.Add(Get(item, "Mac"), Get(item, "Packets"), Get(item, "Protocols"), FormatEpoch(Get(item, "FirstSeen")));
            emptyMessage.Visible = results.Rows.Count == 0;
            hint.Text = results.Rows.Count == 0 ? "Nenhum MAC observado nesta sessão." : $"{results.Rows.Count} MAC(s) observados. Confirme a origem antes de aplicar.";
            return true;
        });
        captureTimer.Stop();
        progress.Value = progress.Maximum;
    }

    private void UpdateProgress() => progress.Value = Math.Min(progress.Maximum, Math.Max(0, (int)(DateTime.UtcNow - captureStarted).TotalSeconds));

    private async Task Plan()
    {
        await Busy(async () => {
            var json = await Run("Plan", "-Adapter", SelectedGuid(), "-Mac", macInput.Text);
            var p = json.RootElement;
            details.Text = $"Adaptador: {Get(p, "Adapter")}{Environment.NewLine}Atual: {Get(p, "CurrentMac")}{Environment.NewLine}Pretendido: {Get(p, "DesiredMac")}{Environment.NewLine}Override atual: {Get(p, "CurrentOverride")}{Environment.NewLine}Backup: {Get(p, "Backup")}{Environment.NewLine}{Environment.NewLine}A interface será reiniciada e a conexão poderá cair.";
            return true;
        });
    }

    private async Task Apply()
    {
        var guid = SelectedGuid();
        JsonDocument plan;
        try { plan = await Run("Plan", "-Adapter", guid, "-Mac", macInput.Text); }
        catch (Exception ex) { ShowError(ex); return; }
        var p = plan.RootElement;
        var message = $"Adaptador: {Get(p, "Adapter")}{Environment.NewLine}MAC atual: {Get(p, "CurrentMac")}{Environment.NewLine}MAC pretendido: {Get(p, "DesiredMac")}{Environment.NewLine}{Environment.NewLine}A conexão será interrompida. Confirme que dispõe de acesso local para restaurar.";
        if (MessageBox.Show(this, message, "Confirmar aplicação", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await Busy(async () => {
            var json = await Run("Apply", "-Adapter", guid, "-Mac", macInput.Text, "-AcknowledgeDisconnect", "-ConfirmedByGui");
            details.Text = "MAC aplicado e verificado: " + Get(json.RootElement, "Mac");
            await RefreshStatus();
            return true;
        });
        await RefreshAdapters();
    }

    private async Task Diagnose()
    {
        await Busy(async () => {
            var json = await Run("Diagnose", "-Adapter", SelectedGuid());
            var p = json.RootElement;
            diagnoseOutput!.Text = $"Link: {Get(p, "Link")}{Environment.NewLine}MAC: {Get(p, "Mac")}{Environment.NewLine}IPv4: {Get(p, "IPv4")}{Environment.NewLine}Gateway: {Get(p, "Gateway")}{Environment.NewLine}DNS: {Get(p, "DnsServers")}{Environment.NewLine}Recebidos: {Get(p, "ReceivedPackets")}{Environment.NewLine}Enviados: {Get(p, "SentPackets")}{Environment.NewLine}{Environment.NewLine}Estes dados não comprovam autorização NAC nem conectividade fim a fim.";
            return true;
        });
    }

    private async Task Restore()
    {
        var guid = SelectedGuid();
        if (MessageBox.Show(this, $"Restaurar o MAC anterior de {activeName}? A interface será reiniciada.", "Confirmar restauração", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await Busy(async () => {
            var json = await Run("Restore", "-Adapter", guid, "-AcknowledgeDisconnect", "-ConfirmedByGui");
            MessageBox.Show(this, "MAC restaurado e verificado: " + Get(json.RootElement, "Mac"), "Restauração concluída");
            await RefreshStatus();
            return true;
        });
        await RefreshAdapters();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        progress.Visible = ClientSize.Width >= 850 || value;
        capture.Enabled = !value && activeStatus?.Equals("Up", StringComparison.OrdinalIgnoreCase) == true;
        apply.Enabled = !value && activeGuid is not null;
        adapters.Enabled = !value;
        if (value) Cursor = Cursors.WaitCursor; else Cursor = Cursors.Default;
    }

    private async Task Busy(Func<Task<bool>> operation)
    {
        SetBusy(true);
        try { await operation(); }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); await RefreshStatus(); }
    }

    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, "PowerR", MessageBoxButtons.OK, MessageBoxIcon.Error);
    private static string Get(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
    private static string FormatEpoch(string value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var epoch)
        ? DateTimeOffset.FromUnixTimeSeconds((long)epoch).ToLocalTime().ToString("HH:mm:ss") : value;

    private string CoreScript()
    {
        if (scriptPath is not null) return scriptPath;
        var folder = Path.Combine(Environment.GetFolderPath(Program.TestMode ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.ProgramFiles), "PowerR", "app");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "PowerR.Core.ps1");
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("PowerR.Core.ps1") ?? throw new InvalidOperationException("Núcleo PowerShell ausente do executável.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var bytes = memory.ToArray();
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
        }
        scriptPath = path;
        return path;
    }

    private async Task<JsonDocument> Run(string action, params string[] args)
    {
        var info = new ProcessStartInfo("powershell.exe") {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", CoreScript(), "-Action", action, "-Json" }.Concat(args)) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Não foi possível iniciar o PowerShell.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Ação {action} falhou." : error.Trim());
        try { return JsonDocument.Parse(output); }
        catch (JsonException) { throw new InvalidOperationException($"Resposta inesperada da ação {action}: {output}"); }
    }

    public async Task SmokeAsync()
    {
        var items = await Run("Adapters");
        var list = items.RootElement.GetProperty("Items");
        if (list.GetArrayLength() < 1) throw new InvalidOperationException("Inventário vazio no smoke test desktop.");
        var guid = Get(list[0], "InterfaceGuid");
        var plan = await Run("Plan", "-Adapter", guid, "-Mac", "02:11:22:33:44:55");
        if (Get(plan.RootElement, "DesiredMac") != "021122334455") throw new InvalidOperationException("Plano inválido no smoke test desktop.");
        var status = await Run("Status", "-Adapter", guid);
        if (Get(status.RootElement, "Guid") != guid) throw new InvalidOperationException("Status inválido no smoke test desktop.");
    }

    private sealed class AdapterItem(JsonElement item)
    {
        public string Name { get; } = Get(item, "Name");
        public string Guid { get; } = Get(item, "InterfaceGuid");
        public string Status { get; } = Get(item, "Status");
        public string Mac { get; } = Get(item, "MacAddress");
        public bool IsPhysicalEthernet { get; } = Get(item, "HardwareInterface") == "True" && Get(item, "PhysicalMediaType") == "802.3";
        public override string ToString() => $"{Name} · {Status} · {Guid}";
    }
}
