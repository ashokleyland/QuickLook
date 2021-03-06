﻿// Copyright © 2017 Paddy Xu
// 
// This file is part of QuickLook program.
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using QuickLook.Helpers;
using QuickLook.Plugin;

namespace QuickLook
{
    public partial class ViewerWindow
    {
        internal void RunWith(string with, string arg)
        {
            if (string.IsNullOrEmpty(_path))
                return;

            try
            {
                Process.Start(new ProcessStartInfo(with)
                {
                    Arguments = arg,
                    WorkingDirectory = Path.GetDirectoryName(_path)
                });
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
            }
        }

        internal void Run()
        {
            if (string.IsNullOrEmpty(_path))
                return;

            try
            {
                Process.Start(new ProcessStartInfo(_path)
                {
                    WorkingDirectory = Path.GetDirectoryName(_path)
                });
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
            }
        }

        internal void RunAndHide()
        {
            Run();
            BeginHide();
        }

        internal void RunAndClose()
        {
            Run();
            BeginClose();
        }

        private static void ResizeAndCenter(Window window, Size size, bool canOldPluginResize, bool canNextPluginResize)
        {
            // resize to MinSize first
            size.Width = Math.Max(size.Width, window.MinWidth);
            size.Height = Math.Max(size.Height, window.MinHeight);

            if (!window.IsLoaded)
            {
                // if the window is not loaded yet, just leave the problem to WPF
                window.Width = size.Width;
                window.Height = size.Height;
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                window.Dispatcher.BeginInvoke(new Action(window.BringToFront), DispatcherPriority.Render);

                return;
            }

            // is the window is now now maximized, do not move it
            if (window.WindowState == WindowState.Maximized)
                return;

            // if this is a new window, place it to top
            if (window.Visibility != Visibility.Visible)
                window.BringToFront();

            var screen = WindowHelper.GetCurrentWindowRect();

            // do not resize or reposition the window is it is visible - unless the next window is size-fixed
            if (window.Visibility == Visibility.Visible && canOldPluginResize && canNextPluginResize)
                return;

            // otherwise, resize it and place it to the old window center.
            var oldCenterX = window.Left + window.Width / 2;
            var oldCenterY = window.Top + window.Height / 2;

            var newLeft = oldCenterX - size.Width / 2;
            var newTop = oldCenterY - size.Height / 2;

            // ensure the new window is fully visible
            newLeft = Math.Max(newLeft, screen.Left); // left
            newTop = Math.Max(newTop, screen.Top); // top
            newLeft = newLeft + size.Width > screen.Right ? screen.Right - size.Width : newLeft; // right
            newTop = newTop + size.Height > screen.Bottom ? screen.Bottom - size.Height : newTop; // bottom

            window.MoveWindow(newLeft, newTop, size.Width, size.Height);
        }

        internal void UnloadPlugin()
        {
            // the focused element will not processed by GC: https://stackoverflow.com/questions/30848939/memory-leak-due-to-window-efectivevalues-retention
            FocusManager.SetFocusedElement(this, null);
            Keyboard.DefaultRestoreFocusMode =
                RestoreFocusMode.None; // WPF will put the focused item into a "_restoreFocus" list ... omg
            Keyboard.ClearFocus();

            _canOldPluginResize = ContextObject.CanResize;

            ContextObject.Reset();

            try
            {
                Plugin?.Cleanup();
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
            Plugin = null;

            _path = string.Empty;
        }

        internal void BeginShow(IViewer matchedPlugin, string path,
            Action<string, ExceptionDispatchInfo> exceptionHandler)
        {
            _path = path;
            Plugin = matchedPlugin;

            ContextObject.ViewerWindow = this;

            // get window size before showing it
            Plugin.Prepare(path, ContextObject);

            SetOpenWithButtonAndPath();

            // revert UI changes
            ContextObject.IsBusy = true;

            var margin = windowFrameContainer.Margin.Top * 2;

            var newHeight = ContextObject.PreferredSize.Height + margin +
                            (ContextObject.TitlebarOverlap ? 0 : windowCaptionContainer.Height);
            var newWidth = ContextObject.PreferredSize.Width + margin;

            ResizeAndCenter(this, new Size(newWidth, newHeight), _canOldPluginResize, ContextObject.CanResize);

            if (Visibility != Visibility.Visible)
                Show();

            ShowWindowCaptionContainer(null, null);
            //WindowHelper.SetActivate(new WindowInteropHelper(this), ContextObject.CanFocus);

            // load plugin, do not block UI
            Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        Plugin.View(path, ContextObject);
                    }
                    catch (Exception e)
                    {
                        exceptionHandler(path, ExceptionDispatchInfo.Capture(e));
                    }
                }),
                DispatcherPriority.Input);
        }

        private void SetOpenWithButtonAndPath()
        {
            buttonOpenWithText.Inlines.Clear();

            if (Directory.Exists(_path))
            {
                AddToInlines("MW_BrowseFolder", Path.GetFileName(_path));
                return;
            }
            var isExe = FileHelper.IsExecutable(_path, out var appFriendlyName);
            if (isExe)
            {
                AddToInlines("MW_Run", appFriendlyName);
                return;
            }
            // not an exe
            var found = FileHelper.GetAssocApplication(_path, out appFriendlyName);
            if (found)
            {
                AddToInlines("MW_OpenWith", appFriendlyName);
                return;
            }
            // assoc not found
            AddToInlines("MW_Open", Path.GetFileName(_path));

            void AddToInlines(string str, string replaceWith)
            {
                // limit str length
                if (replaceWith.Length > 16)
                    replaceWith = replaceWith.Substring(0, 14) + "…" + replaceWith.Substring(replaceWith.Length - 2);

                str = TranslationHelper.GetString(str);
                var elements = str.Split(new[] {"{0}"}, StringSplitOptions.None).ToList();
                while (elements.Count < 2)
                    elements.Add(string.Empty);

                buttonOpenWithText.Inlines.Add(
                    new Run(elements[0]) {FontWeight = FontWeights.Normal}); // text beforehand
                buttonOpenWithText.Inlines.Add(
                    new Run(replaceWith) {FontWeight = FontWeights.SemiBold}); // appFriendlyName
                buttonOpenWithText.Inlines.Add(
                    new Run(elements[1]) {FontWeight = FontWeights.Normal}); // text afterward
            }
        }

        internal void BeginHide()
        {
            UnloadPlugin();

            // if the this window is hidden in Max state, new show() will results in failure:
            // "Cannot show Window when ShowActivated is false and WindowState is set to Maximized"
            WindowState = WindowState.Normal;

            Hide();
            //Dispatcher.BeginInvoke(new Action(Hide), DispatcherPriority.ApplicationIdle);

            ProcessHelper.PerformAggressiveGC();
        }

        internal void BeginClose()
        {
            UnloadPlugin();

            Close();

            ProcessHelper.PerformAggressiveGC();
        }

        internal void Share(object sender, RoutedEventArgs e)
        {
            RunWith("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {_path}");
        }
    }
}