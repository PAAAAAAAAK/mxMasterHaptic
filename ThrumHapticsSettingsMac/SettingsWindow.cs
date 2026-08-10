namespace ThrumHapticsSettingsMac
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Avalonia;
    using Avalonia.Controls;
    using Avalonia.Controls.Primitives;
    using Avalonia.Layout;
    using Avalonia.Media;
    using Avalonia.Threading;

    using Loupedeck.ThrumHapticsPlugin.Config;
    using Loupedeck.ThrumHapticsPlugin.Haptics;

    // The pipe client is compiled in from the Windows settings application, so it
    // keeps that project's namespace.
    using ThrumHapticsSettings;

    /// <summary>
    /// The Thrum Haptics settings window on macOS.
    /// </summary>
    /// <remarks>
    /// A separate application from the Windows settings window on purpose. That
    /// one is WinForms, works, and ships in a 390KB package; rewriting it against
    /// Avalonia would cost roughly 15-20MB and re-open every layout and behaviour
    /// fix that took real device testing to find, for nothing a Windows user would
    /// notice. So the platforms diverge here rather than being unified for its own
    /// sake.
    ///
    /// What they DO share is HapticEvents.cs and Waveforms.cs, compiled into both
    /// and into the plugin. Only values cross the pipe, so a new event appears in
    /// both windows with no UI work and the catalogue cannot drift.
    ///
    /// Behaviour is deliberately identical to the Windows window: sections split
    /// direct input from gestures, paired drag events are tinted together while
    /// staying independently switchable, clicks and scroll are offered only
    /// click-grade waveforms, and selecting a waveform previews it immediately.
    /// </remarks>
    internal sealed class SettingsWindow : Window
    {
        private readonly SettingsClient _client;
        private readonly Dictionary<String, (Boolean Enabled, String Waveform)> _values;
        private readonly System.Threading.CancellationTokenSource _watchdog = new();

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

            public override String ToString() => this.Display;
        }

        public SettingsWindow(SettingsClient client, Dictionary<String, (Boolean, String)> values)
        {
            this._client = client;
            this._values = values;

            this.BuildLayout();
            this.StartPluginWatchdog();
        }

        private (Boolean Enabled, String Waveform) ValueFor(String eventId)
        {
            if (this._values.TryGetValue(eventId, out var value))
            {
                return value;
            }

            var def = HapticEvents.Find(eventId);

            return (def?.DefaultEnabled ?? false, def?.DefaultWaveform ?? Waveforms.SubtleCollision);
        }

        private void BuildLayout()
        {
            this.Title = "Thrum Haptics - Settings";
            this.Width = 700;
            this.MinWidth = 560;
            this.MinHeight = 360;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new DockPanel { LastChildFill = true };

            root.Children.Add(Header());

            var footer = this.Footer();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            // Sections are declared rather than derived, so related categories can
            // share one list. Anything missing here would be invisible, so it is
            // reported rather than lost silently.
            var sections = new (String Title, String[] Categories)[]
            {
                ("Clicks and scroll", new[] { "Clicks", "Scroll" }),
                ("Gestures", new[] { "Gestures" }),
            };

            var body = new StackPanel { Margin = new Thickness(16, 4, 16, 8), Spacing = 4 };

            foreach (var section in sections)
            {
                body.Children.Add(new TextBlock
                {
                    Text = section.Title,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(2, 12, 0, 4),
                });

                body.Children.Add(this.MakeSection(section.Categories));
            }

            var unlisted = UnlistedCategories(sections);

            if (unlisted.Count > 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "Not configurable here: " + String.Join(", ", unlisted),
                    Foreground = Brushes.OrangeRed,
                    Margin = new Thickness(2, 12, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            root.Children.Add(new ScrollViewer
            {
                Content = body,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });

            this.Content = root;

            // Height is derived from the content rather than guessed, then capped,
            // so the list is fully visible on a large display without ever
            // exceeding a small one.
            var rows = HapticEvents.ForCurrentPlatform.Length;
            var wanted = 190 + (rows * 34) + (sections.Length * 34);

            this.Height = Math.Min(wanted, 780);
        }

        private static Control Header() => new StackPanel
        {
            Margin = new Thickness(16, 14, 16, 2),
            Children =
            {
                new TextBlock
                {
                    Text = "Choose which actions buzz, and how they feel.",
                    Opacity = 0.75,
                },
                new TextBlock
                {
                    Text = "Overall strength is set in Logi Options+ under Haptic intensity.",
                    Opacity = 0.75,
                },
            },
        };

        private Control Footer()
        {
            var close = new Button { Content = "Close", MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right };
            close.Click += (_, _) => this.Close();

            var panel = new DockPanel { Margin = new Thickness(16, 8, 16, 14), LastChildFill = false };

            // Changes save as they are made, so there is no Save button and
            // deliberately no Cancel - matching how settings actually persist
            // rather than implying a transaction we do not support.
            var note = new TextBlock
            {
                Text = "Changes are saved automatically.",
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
            };

            DockPanel.SetDock(note, Dock.Left);
            DockPanel.SetDock(close, Dock.Right);

            panel.Children.Add(note);
            panel.Children.Add(close);

            return panel;
        }

        private Control MakeSection(String[] categories)
        {
            var list = new StackPanel();

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

                list.Children.Add(this.MakeRow(def, def.GroupKey != null && shadeGroup));
            }

            return new Border
            {
                Child = list,
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
            };
        }

        private Control MakeRow(HapticEventDef def, Boolean shaded)
        {
            var current = this.ValueFor(def.Id);

            var enabled = new CheckBox
            {
                IsChecked = current.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var waveform = new ComboBox
            {
                ItemsSource = BuildWaveformItems(def.Category),
                Width = 260,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = current.Enabled,
            };

            waveform.SelectedItem = ((IEnumerable<WaveformItem>)waveform.ItemsSource)
                .FirstOrDefault(i => String.Equals(i.Value, current.Waveform, StringComparison.OrdinalIgnoreCase));

            var test = new Button { Content = "Test", MinWidth = 64, VerticalAlignment = VerticalAlignment.Center };

            var label = new TextBlock
            {
                Text = def.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // Handlers attached AFTER the initial values are set. Assigning
            // SelectedItem raises SelectionChanged exactly as a user choice does, so
            // wiring them earlier would make merely opening the window replay every
            // waveform and rewrite every setting - which is precisely what happened
            // on Windows before the same ordering fixed it.
            enabled.IsCheckedChanged += (_, _) =>
            {
                var on = enabled.IsChecked == true;
                waveform.IsEnabled = on;

                this.Apply(def.Id, on, (waveform.SelectedItem as WaveformItem)?.Value, preview: on);
            };

            waveform.SelectionChanged += (_, _) =>
                this.Apply(
                    def.Id,
                    enabled.IsChecked == true,
                    (waveform.SelectedItem as WaveformItem)?.Value,
                    preview: enabled.IsChecked == true);

            test.Click += (_, _) => this.TryPlay((waveform.SelectedItem as WaveformItem)?.Value);

            var grid = new Grid
            {
                Margin = new Thickness(10, 6, 10, 6),
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            };

            Grid.SetColumn(label, 0);
            Grid.SetColumn(enabled, 1);
            Grid.SetColumn(waveform, 2);
            Grid.SetColumn(test, 3);

            enabled.Margin = new Thickness(8, 0, 12, 0);
            waveform.Margin = new Thickness(0, 0, 8, 0);

            grid.Children.Add(label);
            grid.Children.Add(enabled);
            grid.Children.Add(waveform);
            grid.Children.Add(test);

            return new Border
            {
                Child = grid,
                Background = shaded
                    ? new SolidColorBrush(Color.FromArgb(22, 128, 128, 160))
                    : Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
            };
        }

        private void Apply(String eventId, Boolean isEnabled, String waveform, Boolean preview)
        {
            if (String.IsNullOrEmpty(waveform))
            {
                return;
            }

            this._values[eventId] = (isEnabled, waveform);

            try
            {
                this._client.Set(eventId, isEnabled, waveform);
            }
            catch (Exception)
            {
                // The plugin reloads on rebuild and during Options+ restarts.
                // Losing the connection should not take the window down with it.
                return;
            }

            // Live preview: the entire point of choosing a waveform is how it
            // feels, so play it the instant it is selected - or when an event is
            // switched on, so enabling something shows what it does.
            if (preview)
            {
                this.TryPlay(waveform);
            }
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

        private static List<String> UnlistedCategories((String Title, String[] Categories)[] sections)
        {
            var shown = sections
                .SelectMany(s => s.Categories)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return HapticEvents.ForCurrentPlatform
                .Select(e => e.Category)
                .Distinct()
                .Where(c => !shown.Contains(c))
                .ToList();
        }

        private void TryPlay(String waveform)
        {
            if (String.IsNullOrEmpty(waveform))
            {
                return;
            }

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

        /// <summary>
        /// Closes this window once the plugin is gone.
        /// </summary>
        /// <remarks>
        /// Carried over from Windows, where an open window held its own executable
        /// inside the plugin folder and blocked uninstall. macOS can unlink an open
        /// file, so it is not load-bearing here - but a settings window still
        /// talking to a plugin that no longer exists is its own kind of wrong, and
        /// it silently stops saving anything.
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

                    Dispatcher.UIThread.Post(this.Close);

                    return;
                }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            this._watchdog.Cancel();
            base.OnClosed(e);
        }
    }
}
