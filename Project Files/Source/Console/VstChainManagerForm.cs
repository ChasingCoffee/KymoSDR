using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Thetis
{
    public sealed class VstChainManagerForm : Form
    {
        private const int DeferredUiRefreshDelayMs = 200;
        private const int MaxPluginsPerChain = 16;

        private sealed class StatusSnapshot
        {
            public VstHostState RxHostState;
            public VstHostState TxHostState;
            public bool RxReady;
            public bool TxReady;
            public int RxLatencyBlocks;
            public int RxLatencyFloor;
            public int RxSampleRate;
            public int RxBlockSize;
            public int TxLatencyBlocks;
            public int TxLatencyFloor;
            public int TxSampleRate;
            public int TxBlockSize;
        }

        private sealed class ChainPage
        {
            public VstChainKind Kind;
            public TabPage TabPage;
            public CheckBox ChainBypassCheckBox;
            public NumericUpDown GainUpDown;
            public NumericUpDown LatencyFloorUpDown;
            public Label LatencyLabel;
            public Label ChainStatusLabel;
            public ListView PluginListView;
            public Button AddButton;
            public Button RemoveButton;
            public Button MoveUpButton;
            public Button MoveDownButton;
            public Button ToggleEnabledButton;
            public Button ToggleBypassButton;
            public Button OpenEditorButton;
            public Button RefreshButton;
            public Button ExportButton;
            public Button ImportButton;
            public TextBox DetailTextBox;
            public VstChainInfo LastChainInfo;
            public string LastTrackingSignature;
            public bool RefreshInProgress;
            public bool RefreshPending;
            public int PendingPreferredIndex = -1;
            public System.Windows.Forms.Timer DeferredRefreshTimer;
        }

        private readonly Label _summaryLabel;
        private readonly Label _hostStatusLabel;
        private readonly TabControl _tabControl;
        private readonly ChainPage _rxPage;
        private readonly ChainPage _txPage;
        private readonly System.Windows.Forms.Timer _statusTimer;
        private bool _statusRefreshInProgress;
        private bool _updatingUi;
        private bool _openingEditor;

        public VstChainManagerForm()
        {
            Text = "VST Chains";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(820, 520);
            Size = new Size(1024, 620);
            ShowInTaskbar = false;

            TableLayoutPanel rootPanel = new TableLayoutPanel();
            rootPanel.ColumnCount = 1;
            rootPanel.RowCount = 2;
            rootPanel.Dock = DockStyle.Fill;
            rootPanel.Padding = new Padding(8);
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            TableLayoutPanel headerPanel = new TableLayoutPanel();
            headerPanel.ColumnCount = 1;
            headerPanel.RowCount = 2;
            headerPanel.AutoSize = true;
            headerPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            headerPanel.Dock = DockStyle.Fill;
            headerPanel.Margin = new Padding(0);
            headerPanel.Padding = new Padding(0);
            headerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            headerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _summaryLabel = new Label();
            _summaryLabel.AutoSize = true;
            _summaryLabel.Dock = DockStyle.Fill;
            _summaryLabel.Margin = new Padding(0);

            _hostStatusLabel = new Label();
            _hostStatusLabel.AutoSize = true;
            _hostStatusLabel.Dock = DockStyle.Fill;
            _hostStatusLabel.Margin = new Padding(0);
            _hostStatusLabel.Padding = new Padding(0, 2, 0, 2);
            _hostStatusLabel.Font = new Font(_hostStatusLabel.Font, FontStyle.Bold);

            headerPanel.Controls.Add(_summaryLabel, 0, 0);
            headerPanel.Controls.Add(_hostStatusLabel, 0, 1);

            _tabControl = new TabControl();
            _tabControl.Dock = DockStyle.Fill;
            _tabControl.Margin = new Padding(0);

            _rxPage = CreateChainPage(VstChainKind.Rx, "RX Chain");
            _txPage = CreateChainPage(VstChainKind.Tx, "TX Chain");

            _tabControl.TabPages.Add(_rxPage.TabPage);
            _tabControl.TabPages.Add(_txPage.TabPage);

            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 500;
            _statusTimer.Tick += delegate
            {
                if (!Visible || _updatingUi || _statusRefreshInProgress)
                    return;

                RefreshStatusOnlyAsync();
            };

            rootPanel.Controls.Add(headerPanel, 0, 0);
            rootPanel.Controls.Add(_tabControl, 0, 1);
            Controls.Add(rootPanel);

            VstHost.ChainStateChanged += OnChainStateChanged;

            Shown += delegate { _statusTimer.Start(); RefreshChains(); };
            Activated += delegate { RefreshChains(); };
            VisibleChanged += delegate
            {
                if (Visible && IsHandleCreated)
                {
                    _statusTimer.Start();
                    RefreshStatusOnlyAsync();
                }
                else
                {
                    _statusTimer.Stop();
                }
            };
        }

        public void RefreshChains()
        {
            RefreshStatusOnlyAsync();
            RefreshChainPageAsync(_rxPage);
            RefreshChainPageAsync(_txPage);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            base.OnFormClosing(e);
        }

        private ChainPage CreateChainPage(VstChainKind kind, string title)
        {
            ChainPage page = new ChainPage();
            TableLayoutPanel rootPanel = new TableLayoutPanel();
            FlowLayoutPanel headerPanel = new FlowLayoutPanel();
            TableLayoutPanel contentPanel = new TableLayoutPanel();
            FlowLayoutPanel buttonPanel = new FlowLayoutPanel();

            page.Kind = kind;
            page.TabPage = new TabPage(title);
            page.TabPage.Padding = new Padding(6);

            rootPanel.ColumnCount = 1;
            rootPanel.RowCount = 3;
            rootPanel.Dock = DockStyle.Fill;
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));

            headerPanel.AutoSize = true;
            headerPanel.Dock = DockStyle.Fill;
            headerPanel.WrapContents = false;
            headerPanel.Controls.Add(new Label { AutoSize = true, Margin = new Padding(0, 8, 6, 0), Text = "Chain Bypass:" });

            page.ChainBypassCheckBox = new CheckBox();
            page.ChainBypassCheckBox.AutoSize = true;
            page.ChainBypassCheckBox.Margin = new Padding(0, 4, 16, 0);
            page.ChainBypassCheckBox.Text = "Bypass";
            page.ChainBypassCheckBox.CheckedChanged += delegate
            {
                if (_updatingUi) return;
                if (VstHost.SetChainBypass(page.Kind, page.ChainBypassCheckBox.Checked))
                    QueueChainPageRefresh(page, GetSelectedIndex(page));
            };
            headerPanel.Controls.Add(page.ChainBypassCheckBox);

            headerPanel.Controls.Add(new Label { AutoSize = true, Margin = new Padding(0, 8, 6, 0), Text = "Gain:" });

            page.GainUpDown = new NumericUpDown();
            page.GainUpDown.DecimalPlaces = 2;
            page.GainUpDown.Increment = 0.05M;
            page.GainUpDown.Maximum = 8M;
            page.GainUpDown.Minimum = 0M;
            page.GainUpDown.Value = 1.00M;
            page.GainUpDown.Size = new Size(70, 24);
            page.GainUpDown.ValueChanged += delegate
            {
                if (_updatingUi) return;
                if (VstHost.SetChainGain(page.Kind, (double)page.GainUpDown.Value))
                    QueueChainPageRefresh(page, GetSelectedIndex(page));
            };
            headerPanel.Controls.Add(page.GainUpDown);

            headerPanel.Controls.Add(new Label { AutoSize = true, Margin = new Padding(16, 8, 6, 0), Text = "Latency Floor:" });

            page.LatencyFloorUpDown = new NumericUpDown();
            page.LatencyFloorUpDown.DecimalPlaces = 0;
            page.LatencyFloorUpDown.Increment = 1M;
            page.LatencyFloorUpDown.Maximum = 64M;
            page.LatencyFloorUpDown.Minimum = 1M;
            page.LatencyFloorUpDown.Value = kind == VstChainKind.Rx ? 8M : 2M;
            page.LatencyFloorUpDown.Size = new Size(50, 24);
            page.LatencyFloorUpDown.ValueChanged += delegate
            {
                if (_updatingUi) return;
                VstHost.SetPipelineLatencyFloor(page.Kind, (int)page.LatencyFloorUpDown.Value);
            };
            headerPanel.Controls.Add(page.LatencyFloorUpDown);

            page.LatencyLabel = new Label();
            page.LatencyLabel.AutoSize = true;
            page.LatencyLabel.Margin = new Padding(4, 8, 0, 0);
            page.LatencyLabel.Text = "";
            headerPanel.Controls.Add(page.LatencyLabel);

            page.ChainStatusLabel = new Label();
            page.ChainStatusLabel.AutoSize = true;
            page.ChainStatusLabel.Margin = new Padding(16, 8, 0, 0);
            headerPanel.Controls.Add(page.ChainStatusLabel);

            contentPanel.ColumnCount = 2;
            contentPanel.RowCount = 1;
            contentPanel.Dock = DockStyle.Fill;
            contentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            contentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132F));

            page.PluginListView = new ListView();
            page.PluginListView.Dock = DockStyle.Fill;
            page.PluginListView.FullRowSelect = true;
            page.PluginListView.GridLines = true;
            page.PluginListView.HideSelection = false;
            page.PluginListView.MultiSelect = false;
            page.PluginListView.View = View.Details;
            page.PluginListView.Columns.Add("#", 36);
            page.PluginListView.Columns.Add("Plugin", 180);
            page.PluginListView.Columns.Add("Format", 60);
            page.PluginListView.Columns.Add("Load", 110);
            page.PluginListView.Columns.Add("Enabled", 70);
            page.PluginListView.Columns.Add("Bypass", 70);
            page.PluginListView.Columns.Add("Path", 360);
            page.PluginListView.SelectedIndexChanged += delegate { UpdateSelection(page); };
            page.PluginListView.DoubleClick += delegate { OpenSelectedPluginEditor(page); };

            buttonPanel.Dock = DockStyle.Fill;
            buttonPanel.FlowDirection = FlowDirection.TopDown;
            buttonPanel.WrapContents = false;


            page.AddButton = CreateButton("Add VST3...");
            page.AddButton.Click += delegate { AddPluginFromCatalog(page); };
            buttonPanel.Controls.Add(page.AddButton);

            page.RemoveButton = CreateButton("Remove");
            page.RemoveButton.Click += delegate { RemoveSelectedPlugin(page); };
            buttonPanel.Controls.Add(page.RemoveButton);

            page.MoveUpButton = CreateButton("Move Up");
            page.MoveUpButton.Click += delegate { MoveSelectedPlugin(page, -1); };
            buttonPanel.Controls.Add(page.MoveUpButton);

            page.MoveDownButton = CreateButton("Move Down");
            page.MoveDownButton.Click += delegate { MoveSelectedPlugin(page, 1); };
            buttonPanel.Controls.Add(page.MoveDownButton);

            page.ToggleEnabledButton = CreateButton("Enable");
            page.ToggleEnabledButton.Click += delegate { ToggleSelectedPluginEnabled(page); };
            buttonPanel.Controls.Add(page.ToggleEnabledButton);

            page.ToggleBypassButton = CreateButton("Bypass");
            page.ToggleBypassButton.Click += delegate { ToggleSelectedPluginBypass(page); };
            buttonPanel.Controls.Add(page.ToggleBypassButton);

            page.OpenEditorButton = CreateButton("Open Editor");
            page.OpenEditorButton.Click += delegate { OpenSelectedPluginEditor(page); };
            buttonPanel.Controls.Add(page.OpenEditorButton);

            page.RefreshButton = CreateButton("Refresh");
            page.RefreshButton.Click += delegate { RefreshChainPageAsync(page); };
            buttonPanel.Controls.Add(page.RefreshButton);

            page.ExportButton = CreateButton("Export Chain");
            page.ExportButton.Click += delegate { ExportChain(page); };
            buttonPanel.Controls.Add(page.ExportButton);

            page.ImportButton = CreateButton("Import Chain");
            page.ImportButton.Click += delegate { ImportChain(page); };
            buttonPanel.Controls.Add(page.ImportButton);

            page.DeferredRefreshTimer = new System.Windows.Forms.Timer();
            page.DeferredRefreshTimer.Interval = DeferredUiRefreshDelayMs;
            page.DeferredRefreshTimer.Tick += delegate
            {
                page.DeferredRefreshTimer.Stop();
                RefreshChainPageAsync(page, page.PendingPreferredIndex);
            };

            contentPanel.Controls.Add(page.PluginListView, 0, 0);
            contentPanel.Controls.Add(buttonPanel, 1, 0);

            page.DetailTextBox = new TextBox();
            page.DetailTextBox.Dock = DockStyle.Fill;
            page.DetailTextBox.Multiline = true;
            page.DetailTextBox.ReadOnly = true;
            page.DetailTextBox.ScrollBars = ScrollBars.Vertical;
            page.DetailTextBox.Text = "Select a plugin to view its load state and path.";

            rootPanel.Controls.Add(headerPanel, 0, 0);
            rootPanel.Controls.Add(contentPanel, 0, 1);
            rootPanel.Controls.Add(page.DetailTextBox, 0, 2);

            page.TabPage.Controls.Add(rootPanel);
            return page;
        }

        private static Button CreateButton(string text)
        {
            return new Button
            {
                AutoSize = false,
                Size = new Size(108, 28),
                Text = text
            };
        }

        private void UpdateHostStatus(VstHostState rxHostState, VstHostState txHostState)
        {
            string modeText = string.Format(
                "RX {0} | TX {1}",
                VstHost.GetHostStateDisplayName(rxHostState),
                VstHost.GetHostStateDisplayName(txHostState));
            string statusText = "VST3 plugins are supported.";
            if (!VstHost.NativeAvailable || !VstHost.SdkAvailable)
                statusText = VstHost.NativeStatusText + " " + statusText;
            if (!string.IsNullOrWhiteSpace(VstHost.PersistenceStatusText))
                statusText += " " + VstHost.PersistenceStatusText;

            _summaryLabel.Text = statusText;
            _hostStatusLabel.Text = modeText;

            _tabControl.Enabled = VstHost.NativeAvailable;
        }

        private void RefreshStatusOnlyAsync()
        {
            _statusRefreshInProgress = true;
            Task.Run(() =>
            {
                var snapshot = new StatusSnapshot
                {
                    RxHostState = VstHost.GetHostState(VstChainKind.Rx),
                    TxHostState = VstHost.GetHostState(VstChainKind.Tx),
                    RxReady = VstHost.GetChainReady(VstChainKind.Rx),
                    TxReady = VstHost.GetChainReady(VstChainKind.Tx)
                };
                int rxLatBlocks, rxLatFloor, rxSR, rxBS;
                int txLatBlocks, txLatFloor, txSR, txBS;
                VstHost.GetPipelineLatency(VstChainKind.Rx, out rxLatBlocks, out rxLatFloor, out rxSR, out rxBS);
                VstHost.GetPipelineLatency(VstChainKind.Tx, out txLatBlocks, out txLatFloor, out txSR, out txBS);
                snapshot.RxLatencyBlocks = rxLatBlocks;
                snapshot.RxLatencyFloor = rxLatFloor;
                snapshot.RxSampleRate = rxSR;
                snapshot.RxBlockSize = rxBS;
                snapshot.TxLatencyBlocks = txLatBlocks;
                snapshot.TxLatencyFloor = txLatFloor;
                snapshot.TxSampleRate = txSR;
                snapshot.TxBlockSize = txBS;
                return snapshot;
            }).ContinueWith(task =>
            {
                if (IsDisposed || Disposing || !IsHandleCreated)
                {
                    _statusRefreshInProgress = false;
                    return;
                }

                BeginInvoke((MethodInvoker)delegate
                {
                    _statusRefreshInProgress = false;

                    if (task.IsFaulted)
                    {
                        System.Diagnostics.Trace.WriteLine("VST status refresh failed: " +
                            (task.Exception != null ? task.Exception.GetBaseException().Message : "unknown"));
                        return;
                    }

                    if (task.Result == null)
                        return;

                    UpdateHostStatus(task.Result.RxHostState, task.Result.TxHostState);
                    UpdateChainStatusLabel(_rxPage, task.Result.RxReady, GetDisplayedPluginCount(_rxPage), task.Result.RxHostState);
                    UpdateChainStatusLabel(_txPage, task.Result.TxReady, GetDisplayedPluginCount(_txPage), task.Result.TxHostState);
                    UpdateLatencyLabel(_rxPage, task.Result.RxLatencyBlocks, task.Result.RxLatencyFloor, task.Result.RxSampleRate, task.Result.RxBlockSize);
                    UpdateLatencyLabel(_txPage, task.Result.TxLatencyBlocks, task.Result.TxLatencyFloor, task.Result.TxSampleRate, task.Result.TxBlockSize);
                });
            }, TaskScheduler.Default);
        }

        private static void UpdateChainStatusLabel(ChainPage page, bool ready, int pluginCount, VstHostState hostState)
        {
            page.ChainStatusLabel.Text = string.Format(
                "{0}: {1} | {2} ({3} plugin{4})",
                VstHost.GetChainDisplayName(page.Kind),
                ready ? "Ready" : "Not ready",
                VstHost.GetHostStateDisplayName(hostState),
                pluginCount,
                pluginCount == 1 ? string.Empty : "s");
        }

        private void UpdateLatencyLabel(ChainPage page, int currentBlocks, int floorBlocks, int sampleRate, int blockSize)
        {
            double latencyMs = sampleRate > 0 && blockSize > 0
                ? (double)currentBlocks * blockSize / sampleRate * 1000.0
                : 0.0;

            page.LatencyLabel.Text = sampleRate > 0
                ? string.Format("{0} blocks ({1:F1}ms)", currentBlocks, latencyMs)
                : "";

            _updatingUi = true;
            if (floorBlocks >= (int)page.LatencyFloorUpDown.Minimum &&
                floorBlocks <= (int)page.LatencyFloorUpDown.Maximum)
                page.LatencyFloorUpDown.Value = floorBlocks;
            _updatingUi = false;
        }

        private static int GetDisplayedPluginCount(ChainPage page)
        {
            if (page.LastChainInfo != null && page.LastChainInfo.Plugins != null)
                return page.LastChainInfo.Plugins.Count;

            return 0;
        }

        private static bool IsStubRow(VstPluginState plugin)
        {
            return plugin != null && plugin.LoadState == VstPluginLoadState.Failed;
        }

        private static int GetNativePluginIndex(ChainPage page, int rowIndex)
        {
            if (rowIndex < 0)
                return -1;

            List<VstPluginState> requested = VstHost.GetRequestedPlugins(page.Kind);
            if (requested == null || requested.Count == 0)
                return rowIndex;

            HashSet<string> failedPaths = new HashSet<string>();
            Dictionary<int, VstPluginState> failed = VstHost.GetFailedPlugins(page.Kind);
            if (failed != null)
            {
                foreach (VstPluginState s in failed.Values)
                    failedPaths.Add(s.Path ?? string.Empty);
            }

            int row = 0;
            int nativeIdx = 0;
            for (int ri = 0; ri < requested.Count; ri++)
            {
                VstPluginState p = requested[ri];
                if (p == null || string.IsNullOrEmpty(p.Path))
                    continue;

                bool isFailed = failedPaths.Contains(p.Path ?? string.Empty);
                if (row == rowIndex)
                    return isFailed ? -1 : nativeIdx;

                if (!isFailed)
                    nativeIdx++;
                row++;
            }
            return -1;
        }

        private static bool ChainStructureEquals(VstChainInfo previous, VstChainInfo current)
        {
            if (previous == null || current == null)
                return false;
            if (previous.Plugins == null || current.Plugins == null)
                return previous.Plugins == null && current.Plugins == null;
            if (previous.Plugins.Count != current.Plugins.Count)
                return false;

            for (int i = 0; i < current.Plugins.Count; i++)
            {
                string prevPath = previous.Plugins[i] != null ? previous.Plugins[i].Path : null;
                string currPath = current.Plugins[i] != null ? current.Plugins[i].Path : null;
                if (!string.Equals(prevPath, currPath, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static string BuildTrackingSignature(List<VstPluginState> requested, Dictionary<int, VstPluginState> failed)
        {
            if (requested == null || requested.Count == 0)
                return string.Empty;

            HashSet<string> failedPaths = new HashSet<string>();
            if (failed != null)
            {
                foreach (VstPluginState s in failed.Values)
                    failedPaths.Add(s.Path ?? string.Empty);
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < requested.Count; i++)
            {
                VstPluginState p = requested[i];
                if (p == null || string.IsNullOrEmpty(p.Path))
                    continue;
                sb.Append(p.Path);
                sb.Append(failedPaths.Contains(p.Path) ? "F;" : "L;");
            }
            return sb.ToString();
        }

        private void ApplyChainPageRefresh(ChainPage page, VstChainInfo chainInfo, int preferredIndex)
        {
            int selectedIndex = preferredIndex;

            _updatingUi = true;

            try
            {
                List<VstPluginState> requested = VstHost.GetRequestedPlugins(page.Kind);
                Dictionary<int, VstPluginState> failed = VstHost.GetFailedPlugins(page.Kind);
                string trackingSignature = BuildTrackingSignature(requested, failed);
                bool structureUnchanged = ChainStructureEquals(page.LastChainInfo, chainInfo);
                bool trackingUnchanged = string.Equals(trackingSignature, page.LastTrackingSignature, StringComparison.Ordinal);

                page.LastChainInfo = chainInfo;
                page.ChainBypassCheckBox.Checked = chainInfo.Bypass;
                page.GainUpDown.Value = ClampDecimal(chainInfo.Gain, page.GainUpDown.Minimum, page.GainUpDown.Maximum);
                if (chainInfo.LatencyFloorBlocks >= (int)page.LatencyFloorUpDown.Minimum &&
                    chainInfo.LatencyFloorBlocks <= (int)page.LatencyFloorUpDown.Maximum)
                    page.LatencyFloorUpDown.Value = chainInfo.LatencyFloorBlocks;
                UpdateChainStatusLabel(page, chainInfo.Ready, chainInfo.Plugins.Count, chainInfo.HostState);

                if (structureUnchanged && trackingUnchanged)
                {
                    UpdateSelection(page);
                    return;
                }

                page.LastTrackingSignature = trackingSignature;
                page.PluginListView.BeginUpdate();
                page.PluginListView.Items.Clear();

                if (requested != null && requested.Count > 0)
                {
                    HashSet<string> failedPaths = new HashSet<string>();
                    if (failed != null)
                    {
                        foreach (VstPluginState s in failed.Values)
                            failedPaths.Add(s.Path ?? string.Empty);
                    }

                    int loadedIdx = 0;
                    for (int ri = 0; ri < requested.Count; ri++)
                    {
                        VstPluginState requestedPlugin = requested[ri];
                        if (requestedPlugin == null || string.IsNullOrEmpty(requestedPlugin.Path))
                            continue;

                        bool isFailed = failedPaths.Contains(requestedPlugin.Path ?? string.Empty);
                        if (isFailed)
                        {
                            ListViewItem item = new ListViewItem((ri + 1).ToString());
                            item.SubItems.Add(VstHost.GetPluginDisplayName(requestedPlugin));
                            item.SubItems.Add(VstHost.GetPluginFormatDisplayName(requestedPlugin.Format));
                            item.SubItems.Add("Failed");
                            item.SubItems.Add("No");
                            item.SubItems.Add("Yes");
                            item.SubItems.Add("Not Installed");
                            item.Tag = requestedPlugin;
                            item.ForeColor = SystemColors.GrayText;
                            page.PluginListView.Items.Add(item);
                        }
                        else if (loadedIdx < chainInfo.Plugins.Count)
                        {
                            VstPluginState plugin = chainInfo.Plugins[loadedIdx++];
                            ListViewItem item = new ListViewItem((ri + 1).ToString());
                            item.SubItems.Add(VstHost.GetPluginDisplayName(plugin));
                            item.SubItems.Add(VstHost.GetPluginFormatDisplayName(plugin.Format));
                            item.SubItems.Add(VstHost.GetLoadStateDisplayName(plugin.LoadState));
                            item.SubItems.Add(plugin.Enabled ? "Yes" : "No");
                            item.SubItems.Add(plugin.Bypass ? "Yes" : "No");
                            item.SubItems.Add(plugin.Path ?? string.Empty);
                            item.Tag = plugin;
                            page.PluginListView.Items.Add(item);
                        }
                    }

                    for (int i = requested.Count; i < MaxPluginsPerChain; i++)
                    {
                        ListViewItem item = new ListViewItem((i + 1).ToString());
                        item.SubItems.Add(string.Empty);
                        item.SubItems.Add(string.Empty);
                        item.SubItems.Add(string.Empty);
                        item.SubItems.Add(string.Empty);
                        item.SubItems.Add(string.Empty);
                        item.SubItems.Add(string.Empty);
                        item.ForeColor = SystemColors.GrayText;
                        page.PluginListView.Items.Add(item);
                    }
                }
                else
                {
                    for (int i = 0; i < chainInfo.Plugins.Count; i++)
                    {
                        VstPluginState plugin = chainInfo.Plugins[i];
                        ListViewItem item = new ListViewItem((i + 1).ToString());
                        item.SubItems.Add(VstHost.GetPluginDisplayName(plugin));
                        item.SubItems.Add(VstHost.GetPluginFormatDisplayName(plugin.Format));
                        item.SubItems.Add(VstHost.GetLoadStateDisplayName(plugin.LoadState));
                        item.SubItems.Add(plugin.Enabled ? "Yes" : "No");
                        item.SubItems.Add(plugin.Bypass ? "Yes" : "No");
                        item.SubItems.Add(plugin.Path ?? string.Empty);
                        item.Tag = plugin;
                        page.PluginListView.Items.Add(item);
                    }

                    for (int i = chainInfo.Plugins.Count; i < MaxPluginsPerChain; i++)
                    {
                        ListViewItem item = new ListViewItem((i + 1).ToString());
                        item.SubItems.Add(string.Empty);
                        item.SubItems.Add(string.Empty);
                        item.SubItems.Add(string.Empty);
                        item.SubItems.Add(string.Empty);
                        item.SubItems.Add(string.Empty);
                        item.SubItems.Add(string.Empty);
                        item.ForeColor = SystemColors.GrayText;
                        page.PluginListView.Items.Add(item);
                    }
                }

                page.PluginListView.EndUpdate();

                if (selectedIndex >= 0 && selectedIndex < chainInfo.Plugins.Count)
                {
                    page.PluginListView.Items[selectedIndex].Selected = true;
                    page.PluginListView.Items[selectedIndex].Focused = true;
                }
            }
            finally
            {
                _updatingUi = false;
            }

            UpdateSelection(page);
        }

        private void RefreshChainPageAsync(ChainPage page)
        {
            RefreshChainPageAsync(page, GetSelectedIndex(page));
        }

        private void QueueChainPageRefresh(ChainPage page, int preferredIndex)
        {
            page.PendingPreferredIndex = preferredIndex;
            if (page.DeferredRefreshTimer == null)
            {
                RefreshChainPageAsync(page, preferredIndex);
                return;
            }

            page.DeferredRefreshTimer.Stop();
            page.DeferredRefreshTimer.Start();
        }

        private void RefreshChainPageAsync(ChainPage page, int preferredIndex)
        {
            if (page.RefreshInProgress)
            {
                page.RefreshPending = true;
                page.PendingPreferredIndex = preferredIndex;
                return;
            }

            page.RefreshInProgress = true;
            Task.Run(() => VstHost.GetChainInfo(page.Kind)).ContinueWith(task =>
            {
                if (IsDisposed || Disposing || !IsHandleCreated)
                {
                    page.RefreshInProgress = false;
                    return;
                }

                BeginInvoke((MethodInvoker)delegate
                {
                    page.RefreshInProgress = false;

                    if (task.IsFaulted)
                        System.Diagnostics.Trace.WriteLine("VST chain refresh failed: " +
                            (task.Exception != null ? task.Exception.GetBaseException().Message : "unknown"));

                    if (!task.IsFaulted && task.Result != null)
                        ApplyChainPageRefresh(page, task.Result, preferredIndex);

                    if (page.RefreshPending)
                    {
                        page.RefreshPending = false;
                        RefreshChainPageAsync(page, page.PendingPreferredIndex);
                    }
                });
            }, TaskScheduler.Default);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                VstHost.ChainStateChanged -= OnChainStateChanged;

                if (_statusTimer != null)
                {
                    _statusTimer.Stop();
                    _statusTimer.Dispose();
                }
                DisposeChainPageTimer(_rxPage);
                DisposeChainPageTimer(_txPage);
            }

            base.Dispose(disposing);
        }

        private void OnChainStateChanged(VstChainKind kind)
        {
            if (IsDisposed || Disposing || !IsHandleCreated)
                return;

            BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed || Disposing)
                    return;

                ChainPage page = kind == VstChainKind.Rx ? _rxPage : _txPage;
                RefreshChainPageAsync(page);
            });
        }

        private static void DisposeChainPageTimer(ChainPage page)
        {
            if (page == null || page.DeferredRefreshTimer == null)
                return;

            page.DeferredRefreshTimer.Stop();
            page.DeferredRefreshTimer.Dispose();
            page.DeferredRefreshTimer = null;
        }

        private void UpdateSelection(ChainPage page)
        {
            int selectedIndex = GetSelectedIndex(page);
            VstPluginState plugin = GetSelectedPlugin(page);
            bool hasSelection = plugin != null;
            int pluginCount = GetDisplayedPluginCount(page);
            bool chainHasCapacity = pluginCount < MaxPluginsPerChain;
            bool isStub = IsStubRow(plugin);

            page.AddButton.Enabled = chainHasCapacity;
            page.RemoveButton.Enabled = hasSelection;
            page.MoveUpButton.Enabled = hasSelection && !isStub && selectedIndex > 0;
            page.MoveDownButton.Enabled = hasSelection && !isStub && selectedIndex >= 0 && selectedIndex < pluginCount - 1;
            page.ToggleEnabledButton.Enabled = hasSelection && !isStub;
            page.ToggleBypassButton.Enabled = hasSelection && !isStub;
            page.OpenEditorButton.Enabled = hasSelection && !isStub && plugin != null && plugin.LoadState == VstPluginLoadState.Active;

            if (!hasSelection)
            {
                page.ToggleEnabledButton.Text = "Enable";
                page.ToggleBypassButton.Text = "Bypass";
                page.DetailTextBox.Text = "Select a plugin to view its load state and path.";
                return;
            }

            page.ToggleEnabledButton.Text = isStub ? "Enable" : (plugin.Enabled ? "Disable" : "Enable");
            page.ToggleBypassButton.Text = isStub ? "Bypass" : (plugin.Bypass ? "Unbypass" : "Bypass");
            page.DetailTextBox.Text = string.Format(
                "{0}\r\nFormat: {1}\r\nLoad state: {2}\r\nEnabled: {3}\r\nBypass: {4}\r\nPath: {5}",
                VstHost.GetPluginDisplayName(plugin),
                VstHost.GetPluginFormatDisplayName(plugin.Format),
                VstHost.GetLoadStateDisplayName(plugin.LoadState),
                plugin.Enabled ? "Yes" : "No",
                plugin.Bypass ? "Yes" : "No",
                plugin.Path ?? string.Empty);
        }

        private void AddPluginFromCatalog(ChainPage page)
        {
            if (!CanAddPlugin(page))
                return;

            using (VstPluginPickerForm dialog = new VstPluginPickerForm())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                AddPlugin(page, dialog.SelectedPluginPath, dialog.SelectedPluginClassId);
            }
        }

        private void AddPlugin(ChainPage page, string pluginPath, string classId = null)
        {
            VstOperationResult result = VstHost.AddPlugin(page.Kind, pluginPath, classId);
            RefreshChainPageAsync(page, result.PluginIndex);

            if (!result.Success || result.HasWarning)
            {
                MessageBox.Show(
                    this,
                    result.Message,
                    "VST Chains",
                    MessageBoxButtons.OK,
                    result.Success ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
            }
        }

        private bool CanAddPlugin(ChainPage page)
        {
            if (GetDisplayedPluginCount(page) < MaxPluginsPerChain)
                return true;

            MessageBox.Show(
                this,
                string.Format("That chain has reached the maximum of {0} plugins.", MaxPluginsPerChain),
                "Chain Full",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        private void RemoveSelectedPlugin(ChainPage page)
        {
            int selectedIndex = GetSelectedIndex(page);
            VstPluginState plugin = GetSelectedPlugin(page);

            if (selectedIndex < 0 || plugin == null)
                return;

            if (IsStubRow(plugin))
            {
                if (!VstHost.RemoveRequestedPlugin(page.Kind, plugin.Path))
                {
                    MessageBox.Show(this, "The missing plugin entry could not be removed.", "VST Chains", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                RefreshChainPageAsync(page, Math.Max(0, selectedIndex - 1));
                return;
            }

            int nativeIndex = GetNativePluginIndex(page, selectedIndex);
            if (nativeIndex < 0 || !VstHost.RemovePlugin(page.Kind, nativeIndex))
            {
                MessageBox.Show(this, "The native host could not remove the selected plugin.", "VST Chains", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            RefreshChainPageAsync(page, Math.Max(0, selectedIndex - 1));
        }

        private void MoveSelectedPlugin(ChainPage page, int delta)
        {
            int selectedIndex = GetSelectedIndex(page);
            int targetIndex = selectedIndex + delta;

            if (selectedIndex < 0)
                return;

            int fromNativeIndex = GetNativePluginIndex(page, selectedIndex);
            int toNativeIndex = GetNativePluginIndex(page, targetIndex);

            if (fromNativeIndex < 0 || toNativeIndex < 0)
                return;

            if (!VstHost.MovePlugin(page.Kind, fromNativeIndex, toNativeIndex))
            {
                MessageBox.Show(this, "The native host could not reorder the selected plugin.", "VST Chains", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            RefreshChainPageAsync(page, targetIndex);
        }

        private void ToggleSelectedPluginEnabled(ChainPage page)
        {
            int selectedIndex = GetSelectedIndex(page);
            VstPluginState plugin = GetSelectedPlugin(page);

            if (selectedIndex < 0 || plugin == null || IsStubRow(plugin))
                return;

            int nativeIndex = GetNativePluginIndex(page, selectedIndex);
            if (nativeIndex < 0 || !VstHost.SetPluginEnabled(page.Kind, nativeIndex, !plugin.Enabled))
            {
                MessageBox.Show(this, "The native host could not update the plugin enabled state.", "VST Chains", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            QueueChainPageRefresh(page, selectedIndex);
        }

        private void ToggleSelectedPluginBypass(ChainPage page)
        {
            int selectedIndex = GetSelectedIndex(page);
            VstPluginState plugin = GetSelectedPlugin(page);

            if (selectedIndex < 0 || plugin == null || IsStubRow(plugin))
                return;

            int nativeIndex = GetNativePluginIndex(page, selectedIndex);
            if (nativeIndex < 0 || !VstHost.SetPluginBypass(page.Kind, nativeIndex, !plugin.Bypass))
            {
                MessageBox.Show(this, "The native host could not update the plugin bypass state.", "VST Chains", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            QueueChainPageRefresh(page, selectedIndex);
        }

        private void OpenSelectedPluginEditor(ChainPage page)
        {
            int selectedIndex = GetSelectedIndex(page);
            VstPluginState plugin = GetSelectedPlugin(page);

            if (selectedIndex < 0 || plugin == null || IsStubRow(plugin))
                return;

            int nativeIndex = GetNativePluginIndex(page, selectedIndex);
            if (nativeIndex < 0 || plugin.LoadState != VstPluginLoadState.Active)
            {
                MessageBox.Show(this, "Only loaded plugins can open an editor.", "VST Chains", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_openingEditor)
                return;

            _openingEditor = true;
            page.OpenEditorButton.Enabled = false;
            UseWaitCursor = true;

            Task.Run(() => VstHost.OpenPluginEditorWindow(page.Kind, nativeIndex)).ContinueWith(task =>
            {
                if (IsDisposed || Disposing)
                    return;

                BeginInvoke((MethodInvoker)delegate
                {
                    _openingEditor = false;
                    UseWaitCursor = false;
                    UpdateSelection(page);

                    if (task.IsFaulted || !task.Result)
                    {
                        MessageBox.Show(this, "The plugin editor could not be opened.", "VST Chains", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            }, TaskScheduler.Default);
        }

        private void ExportChain(ChainPage page)
        {
            VstProfileState profileState = new VstProfileState();
            VstChainState rxState = VstHost.CaptureChainPersistentState(VstChainKind.Rx);
            VstChainState txState = VstHost.CaptureChainPersistentState(VstChainKind.Tx);
            if (rxState != null && rxState.Plugins != null && rxState.Plugins.Count > 0)
                profileState.Rx = rxState;
            if (txState != null && txState.Plugins != null && txState.Plugins.Count > 0)
                profileState.Tx = txState;

            string activeProfile = VstHost.GetCurrentProfileName();
            string vstDir = VstHost.GetVstDirectoryPath();

            string suggestedName = string.IsNullOrEmpty(activeProfile)
                ? "vst_profile.json"
                : "vst_chains_tx_" + activeProfile + ".json";

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Export Full Profile";
                dialog.Filter = "TX Profile Chains (vst_chains_tx_*.json)|vst_chains_tx_*.json|All JSON Files (*.json)|*.json|All Files (*.*)|*.*";
                dialog.DefaultExt = "json";
                dialog.FileName = suggestedName;
                if (!string.IsNullOrEmpty(vstDir) && Directory.Exists(vstDir))
                    dialog.InitialDirectory = vstDir;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                try
                {
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(profileState, Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText(dialog.FileName, json);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Failed to export chain: " + ex.Message, "Export Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ImportChain(ChainPage page)
        {
            string chainName = VstHost.GetChainDisplayName(page.Kind);
            string vstDir = VstHost.GetVstDirectoryPath();

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Import " + chainName + " Chain";
                dialog.Filter = "TX Profile Chains (vst_chains_tx_*.json)|vst_chains_tx_*.json|All JSON Files (*.json)|*.json|All Files (*.*)|*.*";
                dialog.DefaultExt = "json";
                dialog.CheckFileExists = true;
                if (!string.IsNullOrEmpty(vstDir) && Directory.Exists(vstDir))
                    dialog.InitialDirectory = vstDir;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                try
                {
                    string json = File.ReadAllText(dialog.FileName);
                    Newtonsoft.Json.Linq.JObject obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                    Dictionary<int, VstPluginState> rxFailed = null;
                    Dictionary<int, VstPluginState> txFailed = null;

                    if (obj.Property("Rx") != null || obj.Property("Tx") != null)
                    {
                        VstProfileState profileState = Newtonsoft.Json.JsonConvert.DeserializeObject<VstProfileState>(json);
                        if (profileState == null || (profileState.Rx == null && profileState.Tx == null))
                        {
                            MessageBox.Show(this, "Failed to parse the profile chain file.", "Import Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        if (profileState.Rx != null)
                            rxFailed = VstHost.ApplyChainPersistentState(VstChainKind.Rx, profileState.Rx);
                        if (profileState.Tx != null)
                            txFailed = VstHost.ApplyChainPersistentState(VstChainKind.Tx, profileState.Tx);
                    }
                    else
                    {
                        VstChainState state = Newtonsoft.Json.JsonConvert.DeserializeObject<VstChainState>(json);
                        if (state == null)
                        {
                            MessageBox.Show(this, "Failed to parse the chain file.", "Import Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        if (page.Kind == VstChainKind.Rx)
                            rxFailed = VstHost.ApplyChainPersistentState(VstChainKind.Rx, state);
                        else
                            txFailed = VstHost.ApplyChainPersistentState(VstChainKind.Tx, state);
                    }

                    RefreshChainPageAsync(_rxPage);
                    RefreshChainPageAsync(_txPage);

                    List<string> failedNames = new List<string>();
                    if (rxFailed != null)
                    {
                        foreach (var kv in rxFailed)
                            failedNames.Add(!string.IsNullOrEmpty(kv.Value.Name) ? kv.Value.Name : kv.Value.Path);
                    }
                    if (txFailed != null)
                    {
                        foreach (var kv in txFailed)
                            failedNames.Add(!string.IsNullOrEmpty(kv.Value.Name) ? kv.Value.Name : kv.Value.Path);
                    }

                    if (failedNames.Count > 0)
                    {
                        string msg = "The following plugins could not be loaded:\n\n  - "
                            + string.Join("\n  - ", failedNames)
                            + "\n\nThe plugins may not be installed on this system.\n"
                            + "They are shown at their original position with 'Failed' status.";
                        MessageBox.Show(this, msg, "Plugins Not Found",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Failed to import chain: " + ex.Message, "Import Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static decimal ClampDecimal(double value, decimal minimum, decimal maximum)
        {
            decimal decimalValue = (decimal)value;
            if (decimalValue < minimum) return minimum;
            if (decimalValue > maximum) return maximum;
            return decimalValue;
        }

        private static int GetSelectedIndex(ChainPage page)
        {
            if (page.PluginListView.SelectedIndices.Count == 0)
                return -1;

            return page.PluginListView.SelectedIndices[0];
        }

        private static VstPluginState GetSelectedPlugin(ChainPage page)
        {
            int selectedIndex = GetSelectedIndex(page);
            if (selectedIndex < 0)
                return null;

            return page.PluginListView.Items[selectedIndex].Tag as VstPluginState;
        }
    }
}
