namespace ThrumHapticsSettings
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Forms;

    using Loupedeck.ThrumHapticsPlugin.Config;
    using Loupedeck.ThrumHapticsPlugin.Haptics;

    /// <summary>
    /// The Thrum Haptics settings window.
    /// </summary>
    /// <remarks>
    /// Runs in its own process, bundled inside the plugin package. There is no
    /// second download and no browser tab - the two things that make competing
    /// plugins unpleasant - but it is not hosted inside the Logi Plugin Service,
    /// so a fault here cannot take down every haptic on the machine.
    ///
    /// Split into sections because the halves differ in kind: direct input (a
    /// click, a scroll notch) versus gestures that bracket a movement. Grids are
    /// built from HapticEvents.ForCurrentPlatform, which is compiled into both this
    /// application and the plugin, so new events appear here with no UI work - and
    /// events the OS cannot deliver never appear as switches wired to nothing.
    /// </remarks>
    internal sealed class SettingsForm : Form
    {
        private readonly SettingsClient _client;
        private readonly Dictionary<String, (Boolean Enabled, String Waveform, Int32 Density)> _values;

        private const String ColAction = "action";
        private const String ColEnabled = "enabled";
        private const String ColWaveform = "waveform";
        private const String ColDensity = "density";
        private const String ColTest = "test";

        /// <summary>A waveform as offered in the dropdown.</summary>
        /// <remarks>
        /// Display carries the character hint ("subtle_collision - short") while
        /// Value stays the bare waveform name, so annotating the UI never changes
        /// what gets stored.
        /// </remarks>
        private sealed class WaveformItem
        {
            public String Value { get; init; }

            public String Display { get; init; }
        }

        private readonly System.Threading.CancellationTokenSource _watchdog = new();

        public SettingsForm(SettingsClient client, Dictionary<String, (Boolean, String, Int32)> values)
        {
            this._client = client;
            this._values = values;

            this.BuildLayout();
            this.StartPluginWatchdog();
        }

        /// <summary>
        /// Closes this window once the plugin is gone.
        /// </summary>
        /// <remarks>
        /// This process runs from an executable inside the plugin's own folder, so
        /// while it is alive Windows cannot delete that folder. If the user left
        /// this window open and then uninstalled the plugin, Logi Options+ would
        /// fail to remove it and leave a half-uninstalled plugin on disk.
        ///
        /// Polling on a background thread rather than a UI timer: the ping is a
        /// blocking pipe round trip, and a hung plugin must not freeze the window.
        /// </remarks>
        private void StartPluginWatchdog()
        {
            var token = this._watchdog.Token;

            System.Threading.Tasks.Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));

                    if (token.IsCancellationRequested || this._client.Ping())
                    {
                        continue;
                    }

                    // Plugin has gone: unloaded, reloaded, or being uninstalled.
                    try
                    {
                        this.BeginInvoke(new Action(this.Close));
                    }
                    catch (Exception)
                    {
                        // Window already tearing down; nothing to do.
                    }

                    return;
                }
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            this._watchdog.Cancel();
            base.OnFormClosing(e);
        }

        private (Boolean Enabled, String Waveform, Int32 Density) ValueFor(String eventId)
        {
            if (this._values.TryGetValue(eventId, out var value))
            {
                return value;
            }

            var def = HapticEvents.Find(eventId);

            return (
                def?.DefaultEnabled ?? false,
                def?.DefaultWaveform ?? Waveforms.SubtleCollision,
                HapticEvents.DefaultDensity);
        }

        private void BuildLayout()
        {
            this.Text = "Thrum Haptics - Settings";
            this.StartPosition = FormStartPosition.CenterScreen;

            // From the executable's own embedded icon rather than a separate file:
            // nothing extra to ship, and it cannot go missing from the package.
            try
            {
                this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // Cosmetic. A window with the default icon is worth far less than a
                // window that fails to open.
            }
            this.MinimumSize = new Size(680, 360);
            this.Font = new Font("Segoe UI", 9F);

            // 840, up from 700, for the added spacing column. Columns are
            // proportional here, so a narrow window shrinks every one of them
            // rather than only the flexible one.
            const Int32 defaultWidth = 840;

            this.ClientSize = new Size(defaultWidth, 560);

            var headerText = "Choose which actions buzz, and how they feel."
                           + Environment.NewLine
                           + "Overall strength is set in Logi Options+ under Haptic intensity.";

            var headerPadding = new Padding(12, 10, 12, 8);

            // Height is derived from the font rather than guessed, so the second
            // line is not clipped at larger Windows text scales.
            var header = new Label
            {
                Dock = DockStyle.Top,
                Padding = headerPadding,
                Text = headerText,
                ForeColor = SystemColors.GrayText,
                AutoSize = false,
                Height = (this.Font.Height * 2) + headerPadding.Vertical + 4,
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(8, 0, 8, 0),
            };

            // Sections are declared rather than derived, so related categories can
            // share one grid. Anything missing here would be invisible, so it is
            // reported rather than lost silently.
            var sections = new (String Title, String[] Categories)[]
            {
                ("Clicks and scroll", new[] { "Clicks", "Scroll" }),
                ("Gestures", new[] { "Gestures" }),
            };

            WarnAboutUnlistedCategories(sections);

            var labels = new List<Label>();
            var grids = new List<DataGridView>();
            var heights = new List<Int32>();

            foreach (var section in sections)
            {
                var grid = this.MakeGrid(section.Categories);

                grids.Add(grid);
                heights.Add(NaturalHeight(grid));
                labels.Add(MakeSectionLabel(section.Title));
            }

            // Share space in proportion to each section's row count rather than an
            // even split, which would starve the larger ones.
            var totalGridHeight = (Single)heights.Sum();

            layout.RowCount = sections.Length * 2;

            for (var i = 0; i < sections.Length; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, heights[i] / totalGridHeight * 100F));

                layout.Controls.Add(labels[i], 0, i * 2);
                layout.Controls.Add(grids[i], 0, (i * 2) + 1);
            }

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 46 };

            var closeButton = new Button
            {
                Text = "Close",
                Size = new Size(88, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Location = new Point(this.ClientSize.Width - 108, 8),
            };
            closeButton.Click += (_, _) => this.Close();
            footer.Controls.Add(closeButton);

            // Changes save as they are made, so there is no Save button and
            // deliberately no Cancel - matching how settings actually persist
            // rather than implying a transaction we do not support.
            footer.Controls.Add(new Label
            {
                Dock = DockStyle.Left,
                Width = 340,
                Padding = new Padding(12, 8, 0, 0),
                Text = "Changes are saved automatically.",
                ForeColor = SystemColors.GrayText,
            });

            this.Controls.Add(layout);
            this.Controls.Add(footer);
            this.Controls.Add(header);
            this.AcceptButton = closeButton;

            var chrome = header.Height + footer.Height + layout.Padding.Vertical
                       + labels.Sum(l => l.PreferredHeight + l.Margin.Vertical);

            // Slack below each section's rows: sizing to exactly the content feels
            // cramped, and the empty strip signals the list can grow.
            const Int32 sectionSlackPx = 70;

            var wanted = chrome + heights.Sum() + (sectionSlackPx * sections.Length);
            var maxHeight = (Int32)(Screen.FromPoint(Cursor.Position).WorkingArea.Height * 0.9);

            this.ClientSize = new Size(defaultWidth, Math.Min(wanted, maxHeight));
        }

        private static Label MakeSectionLabel(String text) => new()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(4, 8, 0, 2),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        };

        private DataGridView MakeGrid(String[] categories)
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter,
            };

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = ColAction,
                HeaderText = "Action",
                ReadOnly = true,
                FillWeight = 120,
            });

            grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = ColEnabled,
                HeaderText = "Haptic",
                FillWeight = 45,
            });

            grid.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = ColWaveform,
                HeaderText = "Waveform",
                FillWeight = 185,
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
                DisplayMember = nameof(WaveformItem.Display),
                ValueMember = nameof(WaveformItem.Value),
            });

            // Density applies to the wheels only. The column exists on every grid so
            // the layout stays aligned, and non-wheel rows leave the cell empty and
            // read-only rather than offering a control that does nothing.
            grid.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = ColDensity,
                HeaderText = "Density",
                FillWeight = 80,
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            });

            grid.Columns.Add(new DataGridViewButtonColumn
            {
                Name = ColTest,
                HeaderText = "",
                Text = "Test",
                UseColumnTextForButtonValue = true,
                FillWeight = 45,
            });

            grid.CellContentClick += this.OnCellContentClick;
            grid.CurrentCellDirtyStateChanged += this.OnCurrentCellDirtyStateChanged;

            // NOTE: CellValueChanged is attached AFTER the rows are populated,
            // below. Assigning a cell's value raises it exactly as a user edit
            // does, so subscribing here would make merely opening the window
            // replay every waveform and rewrite every setting.

            // Paired events (a drag's start and end) share a tint so they read as
            // one gesture while staying independently switchable.
            String previousGroup = null;
            var shadeGroup = false;

            foreach (var def in HapticEvents.ForCurrentPlatform.Where(d => categories.Contains(d.Category)))
            {
                if (def.GroupKey != previousGroup)
                {
                    shadeGroup = def.GroupKey != null && !shadeGroup;
                    previousGroup = def.GroupKey;
                }

                var current = this.ValueFor(def.Id);
                var index = grid.Rows.Add(def.DisplayName, current.Enabled, null, null, "Test");
                var row = grid.Rows[index];

                if (def.GroupKey != null && shadeGroup)
                {
                    row.DefaultCellStyle.BackColor = Color.FromArgb(244, 244, 248);
                }

                row.Tag = def.Id;

                // Per-row item list: clicks and scroll repeat constantly, so long
                // waveforms are omitted there rather than offered as a trap.
                var cell = (DataGridViewComboBoxCell)row.Cells[ColWaveform];
                cell.DisplayMember = nameof(WaveformItem.Display);
                cell.ValueMember = nameof(WaveformItem.Value);
                cell.DataSource = BuildWaveformItems(def.Category);
                cell.Value = current.Waveform;

                var densityCell = (DataGridViewComboBoxCell)row.Cells[ColDensity];

                if (def.HasDensity)
                {
                    densityCell.DataSource = HapticEvents.DensityLabels.ToList();
                    densityCell.Value =
                        HapticEvents.DensityLabels[HapticEvents.ClampDensity(current.Density) - 1];
                }
                else
                {
                    // An empty combo cell still paints a dropdown arrow, which reads
                    // as a control that is simply broken. Displaying it as a flat
                    // cell says "not applicable here" instead.
                    densityCell.DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing;
                    densityCell.ReadOnly = true;
                }
            }

            UpdateRowStates(grid);

            grid.CellValueChanged += this.OnCellValueChanged;

            return grid;
        }

        /// <summary>
        /// The waveform choices offered for a category.
        /// </summary>
        /// <remarks>
        /// Clicks and scroll fire constantly, and a long waveform there is not a
        /// matter of taste - it outlasts the action that triggered it and the motor
        /// is still going when the next click arrives. Those are filtered out
        /// rather than left as a trap. Gestures get the full set.
        /// </remarks>
        private static List<WaveformItem> BuildWaveformItems(String category)
        {
            var repeatsConstantly = category is "Clicks" or "Scroll";

            return Waveforms.All
                .Where(w => !repeatsConstantly || Waveforms.IsClickGrade(w))
                .Select(w => new WaveformItem
                {
                    Value = w,
                    Display = $"{w}  -  {Waveforms.CharacterOf(w)}",
                })
                .ToList();
        }

        private static void WarnAboutUnlistedCategories((String Title, String[] Categories)[] sections)
        {
            var shown = sections
                .SelectMany(s => s.Categories)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = HapticEvents.ForCurrentPlatform
                .Select(e => e.Category)
                .Distinct()
                .Where(c => !shown.Contains(c))
                .ToList();

            if (missing.Count > 0)
            {
                MessageBox.Show(
                    "These event categories have no settings section and cannot be "
                    + "configured:" + Environment.NewLine + String.Join(", ", missing),
                    "Thrum Haptics",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        /// <summary>Greys out the waveform of any event that is switched off.</summary>
        private static void UpdateRowStates(DataGridView grid)
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                var isEnabled = row.Cells[ColEnabled].Value is Boolean b && b;

                row.Cells[ColWaveform].ReadOnly = !isEnabled;
                row.Cells[ColWaveform].Style.ForeColor =
                    isEnabled ? SystemColors.ControlText : SystemColors.GrayText;

                // Left alone where it was never populated: making an empty cell
                // editable would hand the user a dropdown with nothing in it.
                if (row.Cells[ColDensity] is DataGridViewComboBoxCell { DataSource: not null } densityCell)
                {
                    densityCell.ReadOnly = !isEnabled;
                    densityCell.Style.ForeColor =
                        isEnabled ? SystemColors.ControlText : SystemColors.GrayText;
                }
            }
        }

        private static Int32 NaturalHeight(DataGridView grid)
        {
            var rows = 0;

            foreach (DataGridViewRow row in grid.Rows)
            {
                rows += row.Height;
            }

            return grid.ColumnHeadersHeight + rows + 4;
        }

        private void OnCurrentCellDirtyStateChanged(Object sender, EventArgs e)
        {
            if (sender is DataGridView grid && grid.IsCurrentCellDirty)
            {
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void OnCellValueChanged(Object sender, DataGridViewCellEventArgs e)
        {
            if (sender is not DataGridView grid || e.RowIndex < 0)
            {
                return;
            }

            var row = grid.Rows[e.RowIndex];

            if (row.Tag is not String eventId)
            {
                return;
            }

            var isEnabled = row.Cells[ColEnabled].Value is Boolean b && b;
            var waveform = row.Cells[ColWaveform].Value as String;

            if (String.IsNullOrEmpty(waveform))
            {
                return;
            }

            // Read back by label rather than kept as an index, so the stored level
            // stays correct if the cell was never populated.
            var densityLabel = row.Cells[ColDensity].Value as String;
            var densityIndex = Array.IndexOf(HapticEvents.DensityLabels, densityLabel);

            var density = densityIndex >= 0 ? densityIndex + 1 : HapticEvents.DefaultDensity;

            this._values[eventId] = (isEnabled, waveform, density);

            try
            {
                this._client.Set(eventId, isEnabled, waveform, density);
            }
            catch (Exception)
            {
                // The plugin reloads on rebuild and during Options+ restarts.
                // Losing the connection should not take the window down with it.
                return;
            }

            var columnName = grid.Columns[e.ColumnIndex].Name;

            if (columnName == ColEnabled)
            {
                UpdateRowStates(grid);
            }

            // Live preview: the entire point of choosing a waveform is how it
            // feels, so play it the instant it is selected - or when an event is
            // switched on, so enabling something shows what it does.
            if (isEnabled && (columnName == ColWaveform || columnName == ColEnabled))
            {
                this.TryPlay(waveform);
            }
        }

        private void OnCellContentClick(Object sender, DataGridViewCellEventArgs e)
        {
            if (sender is not DataGridView grid
                || e.RowIndex < 0
                || grid.Columns[e.ColumnIndex].Name != ColTest)
            {
                return;
            }

            if (grid.Rows[e.RowIndex].Tag is String eventId)
            {
                this.TryPlay(this.ValueFor(eventId).Waveform);
            }
        }

        private void TryPlay(String waveform)
        {
            try
            {
                this._client.Play(waveform);
            }
            catch (Exception)
            {
                // Preview is a convenience; a dropped connection is not worth an
                // error dialog mid-edit.
            }
        }
    }
}
