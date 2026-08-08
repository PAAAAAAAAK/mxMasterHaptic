namespace Loupedeck.MxHapticsPlugin.SettingsUi
{
    using System;
    using System.Drawing;
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
    /// Every event is shown at once in a grid rather than behind a picker, because
    /// comparing rows is the whole point when you are tuning how a mouse feels.
    /// The grid is built from HapticEvents.All, so scroll, hover and system events
    /// appear here automatically as later stages add them.
    /// </remarks>
    internal sealed class SettingsForm : Form
    {
        private readonly HapticSettings _settings;
        private readonly HapticOutput _haptics;
        private DataGridView _grid;

        private const String ColAction = "action";
        private const String ColEnabled = "enabled";
        private const String ColWaveform = "waveform";
        private const String ColTest = "test";

        public SettingsForm(HapticSettings settings, HapticOutput haptics)
        {
            this._settings = settings;
            this._haptics = haptics;

            this.BuildLayout();
            this.PopulateRows();
        }

        private void BuildLayout()
        {
            this.Text = "MX Haptics - Settings";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(660, 380);
            this.MinimumSize = new Size(520, 300);
            this.Font = new Font("Segoe UI", 9F);

            // Height must fit two lines of text PLUS padding. Sizing this by eye is
            // how the second line ended up clipped behind the grid, so it is
            // measured from the font instead: two line heights, the padding, and a
            // couple of pixels of slack. That also survives a user running Windows
            // at a larger text scale, where a hard-coded height would clip again.
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

            this._grid = new DataGridView
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
                BorderStyle = BorderStyle.None,
                EditMode = DataGridViewEditMode.EditOnEnter,
            };

            this._grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = ColAction,
                HeaderText = "Action",
                ReadOnly = true,
                FillWeight = 130,
            });

            this._grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = ColEnabled,
                HeaderText = "Haptic",
                FillWeight = 45,
            });

            var waveformColumn = new DataGridViewComboBoxColumn
            {
                Name = ColWaveform,
                HeaderText = "Waveform",
                FillWeight = 145,
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            };
            waveformColumn.Items.AddRange(Waveforms.All);
            this._grid.Columns.Add(waveformColumn);

            this._grid.Columns.Add(new DataGridViewButtonColumn
            {
                Name = ColTest,
                HeaderText = "",
                Text = "Test",
                UseColumnTextForButtonValue = true,
                FillWeight = 45,
            });

            // CellContentClick fires on checkbox/button hits; CurrentCellDirtyStateChanged
            // is what makes a checkbox or combo commit IMMEDIATELY rather than only
            // when focus leaves the cell - without it a change made and then the
            // window closed would be lost.
            this._grid.CellContentClick += this.OnCellContentClick;
            this._grid.CurrentCellDirtyStateChanged += this.OnCurrentCellDirtyStateChanged;
            this._grid.CellValueChanged += this.OnCellValueChanged;

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 46 };

            var closeButton = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.OK,
                Size = new Size(88, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
            };
            closeButton.Location = new Point(footer.Width - 100, 8);
            closeButton.Click += (_, _) => this.Close();
            footer.Controls.Add(closeButton);

            // Changes are saved the moment they are made, so there is no Save button
            // and deliberately no Cancel - matching how the settings are actually
            // persisted rather than implying a transaction we do not support.
            var note = new Label
            {
                Dock = DockStyle.Left,
                Width = 320,
                Padding = new Padding(12, 8, 0, 0),
                Text = "Changes are saved automatically.",
                ForeColor = SystemColors.GrayText,
            };
            footer.Controls.Add(note);

            this.Controls.Add(this._grid);
            this.Controls.Add(footer);
            this.Controls.Add(header);
            this.AcceptButton = closeButton;
        }

        private void PopulateRows()
        {
            foreach (var def in HapticEvents.All)
            {
                var index = this._grid.Rows.Add(
                    def.EditorLabel,
                    this._settings.IsEnabled(def.Id),
                    this._settings.WaveformFor(def.Id),
                    "Test");

                // Stash the event id on the row so handlers never have to map back
                // from a display label, which would break the moment two events
                // shared a name.
                this._grid.Rows[index].Tag = def.Id;
            }

            this.UpdateRowStates();
        }

        /// <summary>Greys out the waveform of any event that is switched off.</summary>
        private void UpdateRowStates()
        {
            foreach (DataGridViewRow row in this._grid.Rows)
            {
                var isEnabled = row.Cells[ColEnabled].Value is Boolean b && b;

                row.Cells[ColWaveform].ReadOnly = !isEnabled;
                row.Cells[ColWaveform].Style.ForeColor =
                    isEnabled ? SystemColors.ControlText : SystemColors.GrayText;
            }
        }

        private void OnCurrentCellDirtyStateChanged(Object sender, EventArgs e)
        {
            if (this._grid.IsCurrentCellDirty)
            {
                this._grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void OnCellValueChanged(Object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0)
            {
                return;
            }

            var row = this._grid.Rows[e.RowIndex];

            if (row.Tag is not String eventId)
            {
                return;
            }

            var columnName = this._grid.Columns[e.ColumnIndex].Name;

            if (columnName == ColEnabled)
            {
                var isEnabled = row.Cells[ColEnabled].Value is Boolean b && b;
                this._settings.SetEnabled(eventId, isEnabled);
                this.UpdateRowStates();

                // Play it on enable so turning something on immediately shows what
                // it feels like, rather than requiring the user to go try it.
                if (isEnabled)
                {
                    this._haptics.Play(this._settings.WaveformFor(eventId));
                }
            }
            else if (columnName == ColWaveform)
            {
                var waveform = row.Cells[ColWaveform].Value as String;

                if (String.IsNullOrEmpty(waveform))
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
            if (e.RowIndex < 0 || this._grid.Columns[e.ColumnIndex].Name != ColTest)
            {
                return;
            }

            if (this._grid.Rows[e.RowIndex].Tag is String eventId)
            {
                this._haptics.Play(this._settings.WaveformFor(eventId));
            }
        }
    }
}
