namespace Loupedeck.MxHapticsPlugin.SettingsUi
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Forms;

    using Loupedeck.MxHapticsPlugin.Config;
    using Loupedeck.MxHapticsPlugin.Haptics;

    /// <summary>
    /// The plugin's settings window.
    /// </summary>
    /// <remarks>
    /// WHY A WINDOW AT ALL. The Actions SDK has no plugin settings page -
    /// PluginPreferenceType only covers account credentials, and the Action Editor
    /// configures a single bound action at a time, which is the wrong shape for
    /// global preferences (it even offers Save/Cancel semantics we cannot honour).
    ///
    /// Crucially this ships INSIDE the .lplug4. There is no second download and no
    /// browser tab - the two things that make competing plugins unpleasant.
    ///
    /// Split into two sections because the two halves are different in kind:
    /// direct input (a click, a scroll notch) versus gestures that bracket a
    /// movement. Grids are built from HapticEvents.All, so later events appear
    /// automatically in whichever section their category maps to.
    /// </remarks>
    internal sealed class SettingsForm : Form
    {
        private readonly HapticSettings _settings;
        private readonly HapticOutput _haptics;
        private readonly List<DataGridView> _grids = new();

        private const String ColAction = "action";
        private const String ColEnabled = "enabled";
        private const String ColWaveform = "waveform";
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

        public SettingsForm(HapticSettings settings, HapticOutput haptics)
        {
            this._settings = settings;
            this._haptics = haptics;

            this.BuildLayout();
        }

        private void BuildLayout()
        {
            this.Text = "MX Haptics - Settings";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new Size(560, 360);
            this.Font = new Font("Segoe UI", 9F);

            // Width is fixed; height is computed from the content further down, so
            // the window opens showing every row without scrolling. Hard-coding a
            // height would need changing every time an event is added.
            const Int32 defaultWidth = 700;

            this.ClientSize = new Size(defaultWidth, 560);

            var headerText = "Choose which actions buzz, and how they feel."
                           + Environment.NewLine
                           + "Overall strength is set in Logi Options+ under Haptic intensity.";

            var headerPadding = new Padding(12, 10, 12, 8);

            var header = new Label
            {
                Dock = DockStyle.Top,
                Padding = headerPadding,
                Text = headerText,
                ForeColor = SystemColors.GrayText,
                AutoSize = false,
                Height = (this.Font.Height * 2) + headerPadding.Vertical + 4,
            };

            // Two sections, each a label plus its own grid. Proportional rows so
            // both stay visible as the window is resized.
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(8, 0, 8, 0),
            };
            var inputGrid = this.MakeGrid(new[] { "Clicks", "Scroll" });
            var gestureGrid = this.MakeGrid(new[] { "Gestures" });

            var inputHeight = NaturalHeight(inputGrid);
            var gestureHeight = NaturalHeight(gestureGrid);

            // Share space in proportion to how many rows each section actually has,
            // rather than a fixed 50/50 that would starve the larger one as the two
            // sections drift apart in size.
            var total = (Single)(inputHeight + gestureHeight);

            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, inputHeight / total * 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, gestureHeight / total * 100F));

            var inputLabel = MakeSectionLabel("Clicks and scroll");
            var gestureLabel = MakeSectionLabel("Gestures");

            layout.Controls.Add(inputLabel, 0, 0);
            layout.Controls.Add(inputGrid, 0, 1);
            layout.Controls.Add(gestureLabel, 0, 2);
            layout.Controls.Add(gestureGrid, 0, 3);

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

            // Changes are saved the moment they are made, so there is no Save button
            // and deliberately no Cancel - matching how the settings are actually
            // persisted rather than implying a transaction we do not support.
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

            // Size to content so nothing is scrolled off on open, but never taller
            // than the screen will comfortably hold - on a short display, or once
            // enough events exist, the content genuinely will not fit and scrolling
            // is the right answer.
            var chrome = header.Height + footer.Height + layout.Padding.Vertical
                       + inputLabel.PreferredHeight + inputLabel.Margin.Vertical
                       + gestureLabel.PreferredHeight + gestureLabel.Margin.Vertical;

            // Slack below each section's rows. Sizing the window to exactly the
            // content makes it feel cramped, and the empty strip also signals that
            // the list can grow - which it will, as later stages add events.
            const Int32 sectionSlackPx = 90;

            var wanted = chrome + inputHeight + gestureHeight + (sectionSlackPx * 2);
            var maxHeight = (Int32)(Screen.FromPoint(Cursor.Position).WorkingArea.Height * 0.9);

            this.ClientSize = new Size(defaultWidth, Math.Min(wanted, maxHeight));
        }

        /// <summary>Height a grid needs to show every row without scrolling.</summary>
        private static Int32 NaturalHeight(DataGridView grid)
        {
            var rows = 0;

            foreach (DataGridViewRow row in grid.Rows)
            {
                rows += row.Height;
            }

            // +4 for the single-pixel border on each side plus a little slack, so
            // the last row is never clipped into a phantom scrollbar.
            return grid.ColumnHeadersHeight + rows + 4;
        }

        private static Label MakeSectionLabel(String text) => new()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(4, 8, 0, 2),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        };

        /// <summary>Builds a grid containing every event in the given categories.</summary>
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

            // NOTE: CellValueChanged is deliberately attached AFTER the rows are
            // populated, further down. Assigning a cell's value raises it just as a
            // user edit does, so subscribing here would make merely opening the
            // window replay every waveform and rewrite every setting.

            // Paired events (a drag's start and end) are banded with a shared tint
            // so they read as one gesture. They remain independently switchable -
            // the banding communicates the relationship without removing the
            // choice, which is the point: the same button does different jobs in
            // different apps, and wanting only the "grab" is reasonable.
            String previousGroup = null;
            var shadeGroup = false;

            foreach (var def in HapticEvents.All.Where(d => categories.Contains(d.Category)))
            {
                if (def.GroupKey != previousGroup)
                {
                    // Alternate the tint on every group change so adjacent pairs
                    // stay distinguishable from each other.
                    shadeGroup = def.GroupKey != null && !shadeGroup;
                    previousGroup = def.GroupKey;
                }

                var index = grid.Rows.Add(def.DisplayName, this._settings.IsEnabled(def.Id), null, "Test");
                var row = grid.Rows[index];

                if (def.GroupKey != null && shadeGroup)
                {
                    row.DefaultCellStyle.BackColor = Color.FromArgb(244, 244, 248);
                }

                // Stash the event id on the row so handlers never have to map back
                // from a display label, which would break the moment two events
                // shared a name.
                row.Tag = def.Id;

                // Per-row item list: clicks and scroll repeat constantly, so long
                // waveforms are omitted there rather than offered as a trap. In
                // gestures - which happen once per deliberate movement - everything
                // is available.
                var cell = (DataGridViewComboBoxCell)row.Cells[ColWaveform];
                cell.DisplayMember = nameof(WaveformItem.Display);
                cell.ValueMember = nameof(WaveformItem.Value);
                cell.DataSource = BuildWaveformItems(def.Category);
                cell.Value = this._settings.WaveformFor(def.Id);
            }

            UpdateRowStates(grid);

            // Now that every cell holds its stored value, start listening for real
            // user edits.
            grid.CellValueChanged += this.OnCellValueChanged;

            this._grids.Add(grid);

            return grid;
        }

        /// <summary>
        /// The waveform choices offered for a category.
        /// </summary>
        /// <remarks>
        /// Clicks and scroll fire constantly, and a long waveform there is not a
        /// matter of taste - it outlasts the action that triggered it and the
        /// motor is still going when the next click arrives. Those are filtered
        /// out rather than left as a trap. Gestures get the full set.
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

        /// <summary>Greys out the waveform of any event that is switched off.</summary>
        private static void UpdateRowStates(DataGridView grid)
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                var isEnabled = row.Cells[ColEnabled].Value is Boolean b && b;

                row.Cells[ColWaveform].ReadOnly = !isEnabled;
                row.Cells[ColWaveform].Style.ForeColor =
                    isEnabled ? SystemColors.ControlText : SystemColors.GrayText;
            }
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

            var columnName = grid.Columns[e.ColumnIndex].Name;

            if (columnName == ColEnabled)
            {
                var isEnabled = row.Cells[ColEnabled].Value is Boolean b && b;
                this._settings.SetEnabled(eventId, isEnabled);
                UpdateRowStates(grid);

                // Play it on enable so turning something on immediately shows what
                // it feels like, rather than requiring the user to go try it.
                if (isEnabled)
                {
                    this._haptics.Play(this._settings.WaveformFor(eventId));
                }
            }
            else if (columnName == ColWaveform)
            {
                if (row.Cells[ColWaveform].Value is not String waveform || String.IsNullOrEmpty(waveform))
                {
                    return;
                }

                this._settings.SetWaveform(eventId, waveform);

                // Live preview: the entire point of choosing a waveform is how it
                // feels, so play it the instant it is selected.
                this._haptics.Play(waveform);
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
                this._haptics.Play(this._settings.WaveformFor(eventId));
            }
        }
    }
}
