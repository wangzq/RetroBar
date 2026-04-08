using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;
using ManagedShell.Common.Helpers;
using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for TaskList.xaml
    /// </summary>
    public partial class TaskList : UserControl
    {
        private bool isLoaded;
        private bool isScrollable;
        private double DefaultButtonWidth;
        private double MinButtonWidth;
        private double TaskButtonLeftMargin;
        private double TaskButtonRightMargin;
        private ICollectionView taskbarItems;

        public static DependencyProperty ButtonWidthProperty = DependencyProperty.Register(nameof(ButtonWidth), typeof(double), typeof(TaskList), new PropertyMetadata(new double()));

        public double ButtonWidth
        {
            get { return (double)GetValue(ButtonWidthProperty); }
            set { SetValue(ButtonWidthProperty, value); }
        }

        public static DependencyProperty TasksProperty = DependencyProperty.Register(nameof(Tasks), typeof(Tasks), typeof(TaskList), new PropertyMetadata(TasksChangedCallback));

        public Tasks Tasks
        {
            get { return (Tasks)GetValue(TasksProperty); }
            set { SetValue(TasksProperty, value); }
        }

        public static DependencyProperty HostProperty = DependencyProperty.Register(nameof(Host), typeof(Taskbar), typeof(TaskList), new PropertyMetadata(TasksChangedCallback));

        public Taskbar Host
        {
            get { return (Taskbar)GetValue(HostProperty); }
            set { SetValue(HostProperty, value); }
        }

        public TaskList()
        {
            InitializeComponent();
        }

        private void SetStyles()
        {
            DefaultButtonWidth = Application.Current.FindResource("TaskButtonWidth") as double? ?? 0;
            MinButtonWidth = Application.Current.FindResource("TaskButtonMinWidth") as double? ?? 0;
            Thickness buttonMargin;

            if (Settings.Instance.Edge == AppBarEdge.Left || Settings.Instance.Edge == AppBarEdge.Right)
            {
                buttonMargin = Application.Current.FindResource("TaskButtonVerticalMargin") as Thickness? ?? new Thickness();
            }
            else
            {
                buttonMargin = Application.Current.FindResource("TaskButtonMargin") as Thickness? ?? new Thickness();
            }

            TaskButtonLeftMargin = buttonMargin.Left;
            TaskButtonRightMargin = buttonMargin.Right;
        }

        private void TaskList_OnLoaded(object sender, RoutedEventArgs e)
        {
            SetStyles();
            SetTasksCollection();
        }

        private void SetTasksCollection()
        {
            if (!isLoaded && Tasks != null && Host != null)
            {
                taskbarItems = Tasks.CreateGroupedWindowsCollection();
                if (taskbarItems != null)
                {
                    taskbarItems.CollectionChanged += GroupedWindows_CollectionChanged;
                    taskbarItems.Filter = Tasks_Filter;
                }

                TasksList.ItemsSource = taskbarItems;

                Settings.Instance.PropertyChanged += Settings_PropertyChanged;
                Host.hotkeyManager.TaskbarHotkeyPressed += TaskList_TaskbarHotkeyPressed;

                isLoaded = true;
            }
        }

        private static void TasksChangedCallback(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TaskList taskList && e.OldValue == null && e.NewValue != null)
            {
                taskList.SetTasksCollection();
            }
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.MultiMonMode))
            {
                taskbarItems?.Refresh();
            }
            else if (e.PropertyName == nameof(Settings.ShowMultiMon))
            {
                if (Settings.Instance.MultiMonMode != MultiMonOption.AllTaskbars)
                {
                    taskbarItems?.Refresh();
                }
            }
            else if (e.PropertyName == nameof(Settings.AutoSize))
            {
                if (Settings.Instance.AutoSize)
                {
                    UpdateAutoSize();
                    Dispatcher.BeginInvoke(new Action(() => Host?.AdjustVerticalAutoSize()), DispatcherPriority.Loaded);
                }
            }
        }
        private void TaskList_TaskbarHotkeyPressed(object sender, HotkeyManager.TaskbarHotkeyEventArgs e)
        {
            if (Settings.Instance.WinNumHotkeysAction == WinNumHotkeysOption.SwitchTasks && Host.Screen.Primary)
            {
                try
                {
                    bool exists = taskbarItems.MoveCurrentToPosition(e.index);

                    if (exists)
                    {
                        ApplicationWindow window = taskbarItems.CurrentItem as ApplicationWindow;

                        if (e.isShiftPressed)
                        {
                            // Open new instance when Shift is pressed
                            ShellHelper.StartProcess(window.IsUWP ? "appx:" + window.AppUserModelID : window.WinFileName);
                        }
                        else
                        {
                            // Normal behavior - switch to existing window
                            if (window.State == ApplicationWindow.WindowState.Active && window.CanMinimize)
                            {
                                window.Minimize();
                            }
                            else
                            {
                                window.BringToFront();
                            }
                        }
                    }

                }
                catch (ArgumentOutOfRangeException) { }
            }
        }

        private bool Tasks_Filter(object obj)
        {
            if (obj is ApplicationWindow window)
            {
                if (!window.ShowInTaskbar)
                {
                    return false;
                }

                if (!Settings.Instance.ShowMultiMon || Settings.Instance.MultiMonMode == MultiMonOption.AllTaskbars)
                {
                    return true;
                }

                if (Settings.Instance.MultiMonMode == MultiMonOption.SameAsWindowAndPrimary && Host.Screen.Primary)
                {
                    return true;
                }

                IntPtr hMonitor = window.HMonitor;
                if (Host.Screen.Primary && !Host.windowManager.IsValidHMonitor(hMonitor))
                {
                    return true;
                }

                if (hMonitor != Host.Screen.HMonitor)
                {
                    return false;
                }
            }

            return true;
        }

        private void TaskList_OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (taskbarItems != null)
            {
                taskbarItems.CollectionChanged -= GroupedWindows_CollectionChanged;
                taskbarItems.Filter = null;
            }

            if (Host != null)
            {
                Host.hotkeyManager.TaskbarHotkeyPressed -= TaskList_TaskbarHotkeyPressed;
            }

            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;

            isLoaded = false;
        }

        private void GroupedWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            SetTaskButtonWidth();
            UpdateAutoSize();
            if (Settings.Instance.AutoSize && Host != null)
            {
                Dispatcher.BeginInvoke(new Action(() => Host?.AdjustVerticalAutoSize()), DispatcherPriority.Loaded);
            }
        }

        private void TaskList_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            SetTaskButtonWidth();
            UpdateAutoSize();
        }

        private void SetTaskButtonWidth()
        {
            if (Host is null)
                return; // The state is trashed, but presumably it's just a transition

            if (Settings.Instance.Edge == AppBarEdge.Left || Settings.Instance.Edge == AppBarEdge.Right)
            {
                ButtonWidth = ActualWidth;
                SetScrollable(true); // while technically not always scrollable, we don't run into DPI-specific issues with it enabled while vertical
                return;
            }

            double height = ActualHeight;
            int rows = Host.Rows;

            int taskCount = TasksList.Items.Count;
            double margin = TaskButtonLeftMargin + TaskButtonRightMargin;
            double maxWidth = TasksList.ActualWidth / Math.Ceiling((double)taskCount / rows);
            double defaultWidth = DefaultButtonWidth + margin;
            double minWidth = MinButtonWidth + margin;

            if (maxWidth > defaultWidth)
            {
                ButtonWidth = defaultWidth;
                SetScrollable(false);
            }
            else if (maxWidth < minWidth)
            {
                ButtonWidth = Math.Ceiling(defaultWidth / 2);
                SetScrollable(true);
            }
            else
            {
                ButtonWidth = Math.Floor(maxWidth);
                SetScrollable(false);
            }
        }

        private void UpdateAutoSize()
        {
            if (!Settings.Instance.AutoSize || Host == null)
                return;

            int taskCount = TasksList.Items.Count;

            if (taskCount == 0)
            {
                Host.SetAutoSizeValue(1);
                return;
            }

            if (Settings.Instance.Edge == AppBarEdge.Left || Settings.Instance.Edge == AppBarEdge.Right)
            {
                // Vertical: calculate columns needed based on full screen height.
                // Using screen height (stable) instead of viewport height prevents a
                // feedback loop when AdjustVerticalAutoSize shrinks the window.
                double buttonHeight = Host.DesiredRowHeight;
                if (buttonHeight <= 0) return;

                Thickness verticalMargin = Application.Current.FindResource("TaskButtonVerticalMargin") as Thickness? ?? new Thickness();
                double totalButtonHeight = buttonHeight + verticalMargin.Top + verticalMargin.Bottom;

                double nonTaskOverhead = Host.ActualHeight - TasksScrollViewer.ActualHeight;
                double screenLogicalHeight = (Host.Screen.Bounds.Bottom - Host.Screen.Bounds.Top) / Host.DpiScale;
                double availableHeight = screenLogicalHeight - nonTaskOverhead;
                if (availableHeight <= 0) return;

                int buttonsPerColumn = Math.Max(1, (int)Math.Floor(availableHeight / totalButtonHeight));
                int columnsNeeded = (int)Math.Ceiling((double)taskCount / buttonsPerColumn);
                columnsNeeded = Math.Max(1, Math.Min(columnsNeeded, Settings.Instance.TaskbarWidthLimit));
                Host.SetAutoSizeValue(columnsNeeded);
            }
            else
            {
                // Horizontal: calculate rows needed
                double buttonWidth = DefaultButtonWidth + TaskButtonLeftMargin + TaskButtonRightMargin;
                if (buttonWidth <= 0) return;

                double availableWidth = TasksScrollViewer.ActualWidth;
                if (availableWidth <= 0) return;

                int buttonsPerRow = Math.Max(1, (int)Math.Floor(availableWidth / buttonWidth));
                int rowsNeeded = (int)Math.Ceiling((double)taskCount / buttonsPerRow);
                rowsNeeded = Math.Max(1, Math.Min(rowsNeeded, Settings.Instance.RowLimit));
                Host.SetAutoSizeValue(rowsNeeded);
            }
        }

        /// <summary>
        /// Returns the total height of task button content in pre-scale pixels.
        /// </summary>
        public double GetContentHeight()
        {
            int taskCount = TasksList.Items.Count;
            if (taskCount == 0) return 0;

            double total = 0;
            int measured = 0;
            for (int i = 0; i < taskCount; i++)
            {
                var container = TasksList.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (container != null && container.ActualHeight > 0)
                {
                    total += container.ActualHeight + container.Margin.Top + container.Margin.Bottom;
                    measured++;
                }
            }

            if (measured == 0)
            {
                double rowHeight = Application.Current.FindResource("TaskbarRowHeight") as double? ?? 25;
                return taskCount * rowHeight;
            }

            if (measured < taskCount)
            {
                total = total / measured * taskCount;
            }

            return total;
        }

        public double GetViewportHeight() => TasksScrollViewer.ActualHeight;

        private void SetScrollable(bool canScroll)
        {
            if (canScroll == isScrollable) return;

            if (canScroll)
            {
                TasksScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            else
            {
                TasksScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }

            isScrollable = canScroll;
        }

        private void TasksScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (!isScrollable)
            {
                e.Handled = true;
            }
        }
    }
}