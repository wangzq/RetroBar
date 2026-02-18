using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;

namespace RetroBar.Utilities
{
    public class WindowManager : IDisposable
    {
        private static object reopenLock = new object();
        private static readonly int WM_TASKBARCREATEDMESSAGE = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        // UIPI message filter constants
        private const uint WM_COPYDATA = 0x004A;
        private const uint MSGFLT_ALLOW = 1;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

        private bool _isSettingDisplays;
        private int _pendingDisplayEvents;
        private List<AppBarScreen> _screenState = new List<AppBarScreen>();
        private List<Taskbar> _taskbars = new List<Taskbar>();

        private readonly DictionaryManager _dictionaryManager;
        private readonly ExplorerMonitor _explorerMonitor;
        private readonly StartMenuMonitor _startMenuMonitor;
        private readonly ShellManager _shellManager;
        private readonly Updater _updater;
        private HotkeyManager _hotkeyManager;

        public WindowManager(DictionaryManager dictionaryManager, ExplorerMonitor explorerMonitor, ShellManager shellManager, StartMenuMonitor startMenuMonitor, Updater updater, HotkeyManager hotkeyManager)
        {
            _dictionaryManager = dictionaryManager;
            _explorerMonitor = explorerMonitor;
            _shellManager = shellManager;
            _startMenuMonitor = startMenuMonitor;
            _updater = updater;
            _hotkeyManager = hotkeyManager;

            // Allow WM_COPYDATA messages from lower-privilege processes (UIPI bypass).
            // This is needed when RetroBar runs elevated so non-admin apps can register tray icons.
            AllowTrayMessages();

            _shellManager.ExplorerHelper.HideExplorerTaskbar = true;

            openTaskbars();

            // Re-broadcast TaskbarCreated message after hiding Explorer's taskbar.
            // This ensures apps register their tray icons with RetroBar, not Explorer.
            // Use a delayed dispatch to allow Windows to fully process the tray handoff,
            // which can vary based on process elevation and shell state.
            DispatcherTimer delayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            delayTimer.Tick += (s, e) =>
            {
                delayTimer.Stop();
                SendTaskbarCreated();
            };
            delayTimer.Start();

            _explorerMonitor.ExplorerMonitorStart(this, _shellManager);

            Settings.Instance.PropertyChanged += Settings_PropertyChanged;
        }

        private void SendTaskbarCreated()
        {
            // Broadcast TaskbarCreated to all top-level windows so apps re-register their tray icons
            NativeMethods.SendNotifyMessage(
                (IntPtr)NativeMethods.HWND_BROADCAST,
                (uint)WM_TASKBARCREATEDMESSAGE,
                UIntPtr.Zero,
                IntPtr.Zero);
            ShellLogger.Debug("WindowManager: Sent TaskbarCreated message");
        }

        private void AllowTrayMessages()
        {
            // When running elevated, UIPI blocks WM_COPYDATA from non-elevated processes.
            // Tray icons are registered via WM_COPYDATA, so we must allow this message
            // on the tray window to receive icon registrations from non-admin apps.
            IntPtr trayHandle = _shellManager.NotificationArea?.Handle ?? IntPtr.Zero;
            if (trayHandle != IntPtr.Zero)
            {
                bool result = ChangeWindowMessageFilterEx(trayHandle, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
                ShellLogger.Debug($"WindowManager: ChangeWindowMessageFilterEx for WM_COPYDATA on tray window: {result}");
            }
        }

        private void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.ShowMultiMon))
            {
                // Update screen state in case it has changed since last checked
                _screenState = AppBarScreen.FromAllScreens();

                if (_screenState.Count < 2)
                {
                    return;
                }

                ReopenTaskbars();
            }
            else if (e.PropertyName == nameof(Settings.TaskbarMonitorDeviceName))
            {
                // Only relevant in single taskbar mode.
                if (!Settings.Instance.ShowMultiMon)
                {
                    ReopenTaskbars();
                }
            }
        }

        public void ReopenTaskbars()
        {
            lock (reopenLock)
            {
                closeTaskbars();
                openTaskbars();
            }
        }

        public void NotifyWorkAreaChange()
        {
            ShellLogger.Debug($"WindowManager: Work area change notification received");
            handleDisplayChange();
        }

        public void NotifyDisplayChange(ScreenSetupReason reason)
        {
            ShellLogger.Debug($"WindowManager: Display change notification received ({reason})");
            handleDisplayChange();
        }

        private void handleDisplayChange()
        {
            _pendingDisplayEvents++;

            if (_isSettingDisplays)
            {
                return;
            }

            _isSettingDisplays = true;

            while (_pendingDisplayEvents > 0)
            {
                // Skip re-opening taskbars if the screens haven't changed
                if (!haveDisplaysChanged())
                {
                    _pendingDisplayEvents--;
                    continue;
                }

                ReopenTaskbars();

                _pendingDisplayEvents--;
            }

            _isSettingDisplays = false;
            ShellLogger.Debug($"WindowManager: Finished processing display events");
        }

        public bool IsValidHMonitor(IntPtr hMonitor)
        {
            foreach(var screen in _screenState)
            {
                if (screen.HMonitor == hMonitor)
                {
                    return true;
                }
            }

            return false;
        }

        private void closeTaskbars()
        {
            ShellLogger.Debug($"WindowManager: Closing all taskbars");

            foreach (var taskbar in _taskbars)
            {
                taskbar.AllowClose = true;
                taskbar.Close();
            }

            _taskbars.Clear();
        }

        private void openTaskbars()
        {
            _screenState = AppBarScreen.FromAllScreens();

            ShellLogger.Debug($"WindowManager: Opening taskbars");

            if (Settings.Instance.ShowMultiMon)
            {
                foreach (var screen in _screenState)
                {
                    openTaskbar(screen);
                }
            }
            else
            {
                // In single taskbar mode, prefer the user-selected monitor (if it exists), otherwise fall back to primary.
                string desiredDevice = Settings.Instance.TaskbarMonitorDeviceName;
                if (!string.IsNullOrWhiteSpace(desiredDevice))
                {
                    foreach (var screen in _screenState)
                    {
                        if (string.Equals(screen.DeviceName, desiredDevice, StringComparison.OrdinalIgnoreCase))
                        {
                            openTaskbar(screen);
                            return;
                        }
                    }
                }

                openTaskbar(AppBarScreen.FromPrimaryScreen());
            }
        }

        private void openTaskbar(AppBarScreen screen)
        {
            ShellLogger.Debug($"WindowManager: Opening taskbar on screen {screen.DeviceName}");
            Taskbar taskbar = new Taskbar(this, _dictionaryManager, _shellManager, _startMenuMonitor, _updater, _hotkeyManager, screen, Settings.Instance.Edge, Settings.Instance.AutoHide ? AppBarMode.AutoHide : AppBarMode.Normal);
            taskbar.Show();

            _taskbars.Add(taskbar);
        }

        private bool haveDisplaysChanged()
        {
            resetScreenCache();

            if (_screenState.Count == Screen.AllScreens.Length)
            {
                bool same = true;
                for (int i = 0; i < Screen.AllScreens.Length; i++)
                {
                    Screen current = Screen.AllScreens[i];
                    if (!(_screenState[i].Bounds == current.Bounds && _screenState[i].DeviceName == current.DeviceName && _screenState[i].Primary == current.Primary))
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    ShellLogger.Debug("WindowManager: No display changes");
                    return false;
                }
            }

            return true;
        }

        private void resetScreenCache()
        {
            // use reflection to empty screens cache
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var fi = typeof(Screen).GetField("screens", flags) ?? typeof(Screen).GetField("s_screens", flags)
                ?? throw new Exception("Can't find & reset screens cache inside winforms");
            fi.SetValue(null, null);
        }

        public void Dispose()
        {
            _shellManager.ExplorerHelper.HideExplorerTaskbar = false;
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
        }
    }
}
