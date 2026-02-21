using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagedShell.Common.Logging;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Manages custom icon overrides for taskbar buttons.
    /// External applications can request custom icons or overlays for their windows
    /// by sending WM_COPYDATA messages to the RetroBar taskbar window.
    /// </summary>
    public class CustomIconManager
    {
        private static readonly Lazy<CustomIconManager> _instance = new Lazy<CustomIconManager>(() => new CustomIconManager());
        public static CustomIconManager Instance => _instance.Value;

        // Maps window handle to custom icon data
        private readonly ConcurrentDictionary<IntPtr, CustomIconData> _customIcons = new ConcurrentDictionary<IntPtr, CustomIconData>();

        // Event fired when an icon is updated
        public event EventHandler<IntPtr> IconUpdated;

        /// <summary>
        /// Magic number to identify our custom icon messages
        /// </summary>
        public const int CYCMD_SETICON = 0x5242_4943; // "RBIC" - RetroBar Icon Custom
        public const int CYCMD_SETOVERLAY = 0x5242_4F56; // "RBOV" - RetroBar Overlay
        public const int CYCMD_CLEAR = 0x5242_434C; // "RBCL" - RetroBar Clear

        private CustomIconManager() { }

        /// <summary>
        /// Gets the custom icon for a window, if one is set.
        /// </summary>
        public ImageSource GetCustomIcon(IntPtr hwnd)
        {
            if (_customIcons.TryGetValue(hwnd, out var data))
            {
                return data.Icon;
            }
            return null;
        }

        /// <summary>
        /// Gets the custom overlay icon for a window, if one is set.
        /// </summary>
        public ImageSource GetCustomOverlay(IntPtr hwnd)
        {
            if (_customIcons.TryGetValue(hwnd, out var data))
            {
                return data.OverlayIcon;
            }
            return null;
        }

        /// <summary>
        /// Checks if a window has any custom icon set.
        /// </summary>
        public bool HasCustomIcon(IntPtr hwnd)
        {
            return _customIcons.TryGetValue(hwnd, out var data) && data.Icon != null;
        }

        /// <summary>
        /// Checks if a window has a custom overlay set.
        /// </summary>
        public bool HasCustomOverlay(IntPtr hwnd)
        {
            return _customIcons.TryGetValue(hwnd, out var data) && data.OverlayIcon != null;
        }

        /// <summary>
        /// Sets a custom icon for a window from a file path.
        /// </summary>
        public bool SetCustomIcon(IntPtr hwnd, string iconPath)
        {
            try
            {
                var icon = LoadImageFromPath(iconPath);
                if (icon == null)
                {
                    ShellLogger.Warning($"CustomIconManager: Failed to load icon from {iconPath}");
                    return false;
                }

                var data = _customIcons.GetOrAdd(hwnd, _ => new CustomIconData());
                data.Icon = icon;

                IconUpdated?.Invoke(this, hwnd);
                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"CustomIconManager: Failed to set custom icon: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets a custom overlay icon for a window from a file path.
        /// </summary>
        public bool SetCustomOverlay(IntPtr hwnd, string iconPath)
        {
            try
            {
                var icon = LoadImageFromPath(iconPath);
                if (icon == null) return false;

                var data = _customIcons.GetOrAdd(hwnd, _ => new CustomIconData());
                data.OverlayIcon = icon;

                IconUpdated?.Invoke(this, hwnd);
                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"CustomIconManager: Failed to set custom overlay: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clears custom icons for a window.
        /// </summary>
        public void ClearCustomIcons(IntPtr hwnd)
        {
            if (_customIcons.TryRemove(hwnd, out _))
            {
                IconUpdated?.Invoke(this, hwnd);
            }
        }

        /// <summary>
        /// Processes a WM_COPYDATA message for custom icon requests.
        /// </summary>
        /// <param name="lParam">The lParam containing COPYDATASTRUCT pointer</param>
        /// <returns>True if the message was handled</returns>
        public bool ProcessCopyData(IntPtr lParam)
        {
            try
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);

                // Check if this is our message
                int command = (int)cds.dwData;

                if (command != CYCMD_SETICON && command != CYCMD_SETOVERLAY && command != CYCMD_CLEAR)
                {
                    return false;
                }

                if (cds.cbData < IntPtr.Size)
                {
                    ShellLogger.Warning("CustomIconManager: Invalid COPYDATASTRUCT size");
                    return false;
                }

                // Parse the data: first IntPtr is the target window handle
                var data = new byte[cds.cbData];
                Marshal.Copy(cds.lpData, data, 0, cds.cbData);

                IntPtr targetHwnd;
                if (IntPtr.Size == 8)
                {
                    targetHwnd = new IntPtr(BitConverter.ToInt64(data, 0));
                }
                else
                {
                    targetHwnd = new IntPtr(BitConverter.ToInt32(data, 0));
                }

                if (command == CYCMD_CLEAR)
                {
                    ClearCustomIcons(targetHwnd);
                    return true;
                }

                // Rest of the data is the icon path (null-terminated UTF-16 string)
                if (cds.cbData <= IntPtr.Size)
                {
                    ShellLogger.Warning("CustomIconManager: No icon path provided");
                    return false;
                }

                string iconPath = System.Text.Encoding.Unicode.GetString(data, IntPtr.Size, cds.cbData - IntPtr.Size).TrimEnd('\0');
                ShellLogger.Debug($"CustomIconManager: Target hwnd={targetHwnd}, iconPath={iconPath}");

                if (command == CYCMD_SETICON)
                {
                    bool result = SetCustomIcon(targetHwnd, iconPath);
                    ShellLogger.Debug($"CustomIconManager: SetCustomIcon result={result}");
                    return result;
                }
                else if (command == CYCMD_SETOVERLAY)
                {
                    bool result = SetCustomOverlay(targetHwnd, iconPath);
                    ShellLogger.Debug($"CustomIconManager: SetCustomOverlay result={result}");
                    return result;
                }

                return false;
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"CustomIconManager: Error processing WM_COPYDATA: {ex.Message}");
                return false;
            }
        }

        private ImageSource LoadImageFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            // Support for icon files with index (e.g., "shell32.dll,5")
            if (path.Contains(","))
            {
                var parts = path.Split(new[] { ',' }, 2);
                if (parts.Length == 2 && int.TryParse(parts[1], out int index))
                {
                    return LoadIconFromFile(parts[0], index);
                }
            }

            if (!File.Exists(path))
            {
                ShellLogger.Warning($"CustomIconManager: Icon file not found: {path}");
                return null;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".ico")
            {
                return LoadIconFromFile(path, 0);
            }
            else
            {
                // Load as regular image (png, bmp, etc.)
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

        private ImageSource LoadIconFromFile(string path, int index)
        {
            try
            {
                // Use ExtractIconEx for better compatibility
                IntPtr[] largeIcons = new IntPtr[1];
                IntPtr[] smallIcons = new IntPtr[1];
                int count = ExtractIconEx(path, index, largeIcons, smallIcons, 1);

                IntPtr hIcon = largeIcons[0] != IntPtr.Zero ? largeIcons[0] : smallIcons[0];
                if (hIcon == IntPtr.Zero)
                {
                    ShellLogger.Warning($"CustomIconManager: Failed to extract icon from {path},{index}");
                    return null;
                }

                // Create a copy of the icon data before destroying the handle
                using (var icon = System.Drawing.Icon.FromHandle(hIcon))
                using (var bitmap = icon.ToBitmap())
                {
                    var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        bitmap.GetHbitmap(),
                        IntPtr.Zero,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bitmapSource.Freeze();

                    // Clean up icons
                    if (largeIcons[0] != IntPtr.Zero) DestroyIcon(largeIcons[0]);
                    if (smallIcons[0] != IntPtr.Zero && smallIcons[0] != largeIcons[0]) DestroyIcon(smallIcons[0]);

                    return bitmapSource;
                }
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"CustomIconManager: Error loading icon from {path}: {ex.Message}");
                return null;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, int nIcons);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        private class CustomIconData
        {
            public ImageSource Icon { get; set; }
            public ImageSource OverlayIcon { get; set; }
        }
    }
}
