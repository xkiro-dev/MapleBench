using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MapleBench.Desktop;

/// <summary>
/// The desktop window.
///
/// MapleBench's UI is HTML either way; this hosts it in a real application
/// window instead of a browser tab, which buys three things a tab cannot give:
/// no address bar or browser chrome, native Windows file and folder dialogs,
/// and a window that closes the editor when you close it.
///
/// WebView2 needs the Edge runtime, which ships with Windows 10/11 but can be
/// absent on stripped installs. <see cref="TryRun"/> reports that rather than
/// crashing, so the caller can fall back to the browser.
///
/// <para><b>Startup shape.</b> Bringing up WebView2 is the slowest thing in a
/// cold launch by a wide margin: <c>CoreWebView2Environment.CreateAsync</c> plus
/// <c>EnsureCoreWebView2Async</c> spawn the whole Chromium process tree and, on
/// a first run, build a browser profile from nothing. Kestrel is ready long
/// before that finishes. Two things are done about it:</para>
/// <list type="number">
///   <item><description><see cref="BeginWarmup"/> starts the window and the
///   WebView2 environment without a URL, so that work can overlap the host's own
///   startup instead of queueing behind it. <see cref="TryRun"/> then just hands
///   over the URL. It is optional: called or not, the behaviour is the same, only
///   the timing differs.</description></item>
///   <item><description>The window paints a real splash from its first frame, so
///   the wait is a branded window rather than the blank grey rectangle it used to
///   be. It is replaced the moment the page has a DOM, not when it has finished
///   loading.</description></item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopShell : Form
{
    /// <summary>
    /// The page background from tokens.css. The splash and the form share it so
    /// there is no flash when one gives way to the other.
    /// </summary>
    private static readonly System.Drawing.Color Page = System.Drawing.Color.FromArgb(0xF2, 0xF4, 0xF7);

    private readonly WebView2 _view;
    private readonly SplashPanel _splash;

    /// <summary>
    /// Completed when the caller knows the URL. Null until then: with
    /// <see cref="BeginWarmup"/> the window exists before the server has a port.
    /// </summary>
    private readonly TaskCompletionSource<string> _urlKnown =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string? _url;
    private bool _closeConfirmed;
    private string? _failure;

    // --- warmup state, all touched under _warmupGate -------------------------
    private static readonly object _warmupGate = new();
    private static DesktopShell? _warm;
    private static Thread? _warmThread;
    private static string? _warmFailure;
    private static bool _warmupStarted;
    /// <summary>Set once the warm thread has a window, or has failed to get one.</summary>
    private static readonly ManualResetEventSlim _warmSettled = new(false);

    private static readonly Stopwatch _sinceStart = Stopwatch.StartNew();
    private static readonly bool _trace =
        Environment.GetEnvironmentVariable("MAPLEBENCH_TRACE_STARTUP") == "1";

    /// <summary>
    /// Prints a startup phase timing when MAPLEBENCH_TRACE_STARTUP=1.
    ///
    /// Off by default and one environment-variable read to decide. It exists
    /// because "the window took a while to appear" is not a number anyone can
    /// act on, and every attempt to measure this from outside the process can
    /// only see the window handle, never what the window is waiting for.
    /// </summary>
    private static void Trace(string phase)
    {
        if (_trace) Console.Error.WriteLine($"[startup] {_sinceStart.ElapsedMilliseconds,6} ms  {phase}");
    }

    private DesktopShell(string? url)
    {
        _url = url;

        Text = "MapleBench";
        Width = 1440;
        Height = 900;
        MinimumSize = new System.Drawing.Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        // Matches the app's own page background so the first paint does not
        // flash white before the UI loads.
        BackColor = Page;

        System.Drawing.Icon? appIcon = null;
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "MapleBench.ico");
            if (File.Exists(iconPath))
                Icon = appIcon = new System.Drawing.Icon(iconPath);
        }
        catch { /* the default icon is fine */ }

        _view = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_view);

        // Sits on top of the WebView2 rather than instead of it: the control has
        // to be realised and visible for EnsureCoreWebView2Async to attach to
        // it, so hiding it to show a splash would deadlock the thing we are
        // waiting for. Covering it costs nothing and is not a special case.
        _splash = new SplashPanel(appIcon) { Dock = DockStyle.Fill };
        Controls.Add(_splash);
        _splash.BringToFront();
    }

    /// <summary>
    /// Starts the desktop window and the WebView2 environment before the URL is
    /// known, so that work runs alongside the host's startup instead of after it.
    ///
    /// Call this as early in the process as possible — the earlier it is, the
    /// more of Chromium's start-up is already paid for by the time the server is
    /// listening. It returns immediately; the window comes up on its own UI
    /// thread showing the splash, and waits there for <see cref="TryRun"/> to
    /// hand it a URL.
    ///
    /// Calling it more than once is a no-op. Not calling it at all is also fine:
    /// <see cref="TryRun"/> does the whole job itself in that case, exactly as it
    /// did before this existed.
    ///
    /// Only call it when the desktop shell is actually the chosen mode. In
    /// --browser or --no-browser runs it would spawn a Chromium process tree and
    /// create a browser profile that nothing goes on to use.
    /// </summary>
    public static void BeginWarmup()
    {
        lock (_warmupGate)
        {
            if (_warmupStarted) return;
            _warmupStarted = true;

            try
            {
                // Throws if the runtime is missing. Doing it here means a machine
                // without WebView2 never gets a window it cannot fill, and TryRun
                // reports the same failure it would have reported anyway.
                CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception ex)
            {
                _warmFailure = ex.Message;
                return;
            }

            // Everything about the window is built *inside* the thread that will
            // pump it, and nothing about it is touched from out here.
            //
            // This is not tidiness. Constructing the Form and its WebView2 on the
            // calling thread and only running the message loop on the new one
            // looks equivalent and is not: the WebView2 control is a COM object
            // whose apartment is decided where it is created, and attaching it
            // from a loop on a different thread fails several seconds later with
            // "Unable to cast to ICoreWebView2Controller2" -- an error that reads
            // like an SDK/runtime version mismatch and is nothing of the kind.
            _warmThread = new Thread(() =>
            {
                DesktopShell shell;
                try
                {
                    ApplicationConfiguration.Initialize();
                    shell = new DesktopShell(null);
                }
                catch (Exception ex)
                {
                    lock (_warmupGate) { _warmFailure = ex.Message; }
                    _warmSettled.Set();
                    return;
                }

                lock (_warmupGate) { _warm = shell; }
                _warmSettled.Set();

                try { Application.Run(shell); }
                catch (Exception ex) { shell._failure ??= ex.Message; }
            })
            {
                // Background, because a warmed window must never be the reason
                // the process refuses to exit if the host decides not to use the
                // desktop shell after all. TryRun joins it when it does use it,
                // so the "window closed, shut the server down" path is unchanged.
                IsBackground = true,
                Name = "MapleBench desktop shell",
            };
            _warmThread.SetApartmentState(ApartmentState.STA);
            _warmThread.Start();
            Trace("warmup thread started");
        }
    }

    /// <summary>
    /// Opens the window and blocks until it closes. Returns false when WebView2
    /// is unavailable or failed to come up, leaving the caller free to open a
    /// browser instead.
    ///
    /// If <see cref="BeginWarmup"/> ran, the window is already on screen and this
    /// only hands it the URL; otherwise it builds the window here, which is what
    /// it has always done.
    /// </summary>
    public static bool TryRun(string url, out string? failure)
    {
        DesktopShell? warm;
        Thread? thread;
        lock (_warmupGate)
        {
            warm = _warm;
            thread = _warmThread;
        }

        // A warm thread exists but has not published its window yet. Wait for it
        // rather than racing past into building a second one.
        if (thread is not null && warm is null)
        {
            _warmSettled.Wait();
            lock (_warmupGate) { warm = _warm; }
        }

        lock (_warmupGate)
        {
            if (_warmFailure is not null)
            {
                // Warmup already established WebView2 is not usable. Do not probe
                // a second time: the answer will not have changed and the caller
                // is waiting to fall back.
                failure = _warmFailure;
                return false;
            }
        }

        if (warm is not null && thread is not null)
        {
            Trace("url handed to warmed window");
            warm.Accept(url);
            thread.Join();
            failure = warm._failure;
            return failure is null;
        }

        try
        {
            // Throws if the runtime is missing, which is the case we want to
            // detect before a window ever appears.
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }

        DesktopShell? shell = null;
        try
        {
            ApplicationConfiguration.Initialize();
            shell = new DesktopShell(url);
            Application.Run(shell);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }

        // A failure recorded while the window was up — a locked browser profile
        // is the usual one, and it used to reach the user as a crash because it
        // was thrown from an async void handler. The browser fallback handles it
        // far better than a stack trace does.
        failure = shell._failure;
        return failure is null;
    }

    /// <summary>Hands a URL to a window that came up before the server did.</summary>
    private void Accept(string url)
    {
        _url = url;
        try
        {
            if (IsHandleCreated) BeginInvoke(() => _urlKnown.TrySetResult(url));
            else _urlKnown.TrySetResult(url);
        }
        catch (ObjectDisposedException)
        {
            // The window was closed before it was ever used. Nothing to navigate.
            _urlKnown.TrySetResult(url);
        }
    }

    /// <summary>The profile folder, created if it does not exist yet.</summary>
    private static string PrepareProfile()
    {
        // Keep the browser profile beside the app rather than in the user's
        // AppData for Edge itself, so uninstalling is a folder delete.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Path.Combine(local, "MapleBench", "WebView2");

        // This folder holds the LevelDB store behind localStorage: pins, layout,
        // recents, last directory, theme, sort mode and the session-restore
        // list. Deleting it is how you reset the app's remembered state.
        Directory.CreateDirectory(profile);
        return profile;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        CoreWebView2 core;
        try
        {
            string profile = PrepareProfile();
            Trace("profile ready");

            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            Trace("WebView2 environment created");

            await _view.EnsureCoreWebView2Async(environment);
            Trace("WebView2 attached");

            core = _view.CoreWebView2;
        }
        catch (Exception ex)
        {
            // Reaching here used to be an unhandled exception out of an async
            // void handler, i.e. a crash with no message. The commonest cause is
            // a second copy of MapleBench holding the browser profile. Say so,
            // then close so TryRun can fall back to the browser rather than
            // leaving the user with a window that will never fill.
            _failure = ex.Message;
            _splash.ShowFailure(ex.Message);
            // Long enough to read, short enough not to feel stuck. The browser
            // fallback opens straight after.
            await Task.Delay(TimeSpan.FromSeconds(4));
            _closeConfirmed = true;
            Close();
            return;
        }

        // This is an application window, not a browser: no context menu of its
        // own (the app draws one), no dev tools by default, no Ctrl+F bar.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;

        // Anything that is not our own local UI opens in the real browser.
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternally(args.Uri);
        };

        core.WebMessageReceived += OnWebMessage;

        // Drop the splash as soon as the page has a DOM rather than waiting for
        // NavigationCompleted: by DOMContentLoaded the app has already painted
        // its shell, and holding the splash over it would add a stall of our own
        // to the one this is here to hide. NavigationCompleted is kept as the
        // backstop for a navigation that ends without a DOM at all.
        core.DOMContentLoaded += (_, _) => RevealPage();
        core.NavigationCompleted += (_, _) => RevealPage();

        // Bridge: the page asks for a native dialog, we answer with a path.
        await core.AddScriptToExecuteOnDocumentCreatedAsync("""
            window.mapleBenchDesktop = {
              version: 1,
              _pending: new Map(),
              _next: 1,
              _call(kind, payload) {
                const id = this._next++;
                return new Promise((resolve) => {
                  this._pending.set(id, resolve);
                  window.chrome.webview.postMessage({ id, kind, ...payload });
                });
              },
              pickFolder(title) { return this._call('pickFolder', { title }); },
              pickFile(title, filter) { return this._call('pickFile', { title, filter }); },
              savePath(title, suggested, filter) {
                return this._call('savePath', { title, suggested, filter });
              },
              showInExplorer(path) { return this._call('showInExplorer', { path }); },
            };
            window.chrome.webview.addEventListener('message', (event) => {
              const { id, result } = event.data || {};
              const resolve = window.mapleBenchDesktop._pending.get(id);
              if (resolve) { window.mapleBenchDesktop._pending.delete(id); resolve(result); }
            });
            """);

        core.DocumentTitleChanged += (_, _) =>
            Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "MapleBench" : core.DocumentTitle;

        // With BeginWarmup everything above has already happened by the time the
        // server has a port, and this is where the window waits. Without it the
        // URL was known from the start and this completes immediately.
        if (_url is null)
        {
            Trace("waiting for the server's URL");
            _url = await _urlKnown.Task;
        }

        Trace("navigating");
        core.Navigate(_url);
    }

    /// <summary>Takes the splash down. Safe to call more than once.</summary>
    private void RevealPage()
    {
        if (_splash.IsDisposed || !_splash.Visible) return;
        Trace("page has a DOM; splash removed");
        _splash.Visible = false;
        _splash.SendToBack();
    }

    /// <summary>
    /// Guards against closing the window on top of unsaved edits.
    ///
    /// The page's own `beforeunload` handler never fires here: closing a
    /// WinForms host is not a page unload. Without this the single most likely
    /// way to lose a day's work is clicking the X.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Already asked, or the app is shutting us down: let it go.
        if (_closeConfirmed || e.CloseReason != CloseReason.UserClosing)
        {
            base.OnFormClosing(e);
            return;
        }

        int dirty = CountDirtyFiles();
        if (dirty == 0)
        {
            base.OnFormClosing(e);
            return;
        }

        e.Cancel = true;
        DialogResult answer = MessageBox.Show(
            this,
            $"{dirty} open file{(dirty == 1 ? " has" : "s have")} changes that have not been written to disk.\n\n" +
            "Closing now discards them. Nothing on disk has been altered.",
            "Discard unsaved changes?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);   // default is No

        if (answer != DialogResult.Yes)
            return;

        _closeConfirmed = true;
        BeginInvoke(Close);
    }

    /// <summary>
    /// Asks the running service how many open files are dirty.
    ///
    /// Deliberately synchronous and in-process: this is called from a closing
    /// handler that must decide before returning, and awaiting the page over
    /// the WebView2 bridge there would deadlock the UI thread.
    /// </summary>
    private int CountDirtyFiles()
    {
        // Warmed up but never handed a URL: there is no server to ask and
        // nothing has been opened, so nothing can be dirty.
        if (string.IsNullOrEmpty(_url)) return 0;

        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(3) };
            string json = client.GetStringAsync($"{_url}/api/files").GetAwaiter().GetResult();
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateArray()
                .Count(file => file.TryGetProperty("dirty", out JsonElement d) && d.GetBoolean());
        }
        catch (TaskCanceledException)
        {
            // Listening but too busy to answer: /api/files takes the session gate,
            // and a save holds it for the whole write. Closing the window during one
            // is precisely when the prompt matters, so report *something* dirty and
            // let the user decide. Returning 0 here dismissed the warning at the one
            // moment it was load-bearing.
            return 1;
        }
        catch
        {
            // Anything else — not listening, or an answer we cannot read. A false
            // prompt on every close is worse than the rare missed warning.
            return 0;
        }
    }

    /// <summary>
    /// Serves the native-dialog requests the page makes.
    ///
    /// The dialog is opened from a POSTED continuation, never inline.
    ///
    /// Every one of these ends in <c>ShowDialog</c>, which is modal and runs its
    /// own message loop until the user answers. Running that from inside a
    /// WebView2 callback re-enters the message loop while the browser is still
    /// dispatching — and the observable result is a Browse button that does
    /// nothing: the dialog opens behind the window, or never appears, and the
    /// promise on the page never settles because the reply is only posted after
    /// a dialog that is not on screen has been dismissed.
    ///
    /// Returning first lets WebView2 finish its dispatch; the dialog then opens
    /// from an ordinary message-loop turn with the window as its owner, which is
    /// the only context a modal dialog is safe to open in.
    /// </summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        int id;
        string kind;
        string? title, filter, suggested, path;

        // Read everything out before returning. The JsonDocument owns pooled
        // buffers and the event args are only valid for the length of this
        // call, so a JsonElement captured into the closure below would be
        // reading memory that has gone back to the pool by the time it runs.
        try
        {
            using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement message = document.RootElement;
            if (!message.TryGetProperty("id", out JsonElement idElement))
                return;

            id = idElement.GetInt32();
            kind = message.TryGetProperty("kind", out JsonElement k) ? k.GetString() ?? "" : "";
            title = TextOf(message, "title");
            filter = TextOf(message, "filter");
            suggested = TextOf(message, "suggested");
            path = TextOf(message, "path");
        }
        catch
        {
            return;
        }

        BeginInvoke(() =>
        {
            string? result;
            try
            {
                // The click that triggered this went to the WebView2 child, so
                // the form itself may not be the active window — and a modal
                // dialog owned by an inactive window opens behind it.
                if (!Focused && CanFocus)
                    Activate();

                result = kind switch
                {
                    "pickFolder" => PickFolder(title),
                    "pickFile" => PickFile(title, filter),
                    "savePath" => SavePath(title, suggested, filter),
                    "showInExplorer" => Reveal(path),
                    _ => null,
                };
            }
            catch
            {
                // A dialog that throws must still answer. The page awaits this
                // promise, and never resolving it leaves the button dead for the
                // rest of the session with nothing on screen to say why.
                result = null;
            }

            try
            {
                _view.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new { id, result }));
            }
            catch (ObjectDisposedException)
            {
                // The window closed while the dialog was open; nobody is waiting.
            }
            catch (InvalidOperationException)
            {
                // Same, when the core has already been torn down.
            }
        });
    }

    private static string? TextOf(JsonElement message, string name) =>
        message.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    private string? PickFolder(string? title)
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = title ?? "Choose your MapleStory folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private string? PickFile(string? title, string? filter)
    {
        using OpenFileDialog dialog = new()
        {
            Title = title ?? "Open a WZ file",
            Filter = filter ?? "MapleStory archives (*.wz;*.ms;*.img)|*.wz;*.ms;*.img|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private string? SavePath(string? title, string? suggested, string? filter)
    {
        using SaveFileDialog dialog = new()
        {
            Title = title ?? "Save as",
            FileName = suggested ?? "",
            Filter = filter ?? "MapleStory archive (*.wz)|*.wz|All files (*.*)|*.*",
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private static string? Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) && !Directory.Exists(path))
            return null;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void OpenExternally(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* nothing useful to do if the shell refuses */ }
    }

    /// <summary>
    /// What the window shows while WebView2 is coming up.
    ///
    /// This is the fix for the longest-standing thing wrong with a cold launch:
    /// the window used to appear the instant the form was shown and then sit as
    /// an empty grey rectangle for as long as Chromium took to start — seconds,
    /// on a first run where the browser profile is built from nothing. A blank
    /// window with a title bar is indistinguishable from a hung one.
    ///
    /// Drawn rather than composed from controls so it costs one paint and no
    /// layout, and so it is on screen in the window's first frame.
    /// </summary>
    private sealed class SplashPanel : Panel
    {
        private static readonly System.Drawing.Color Ink = System.Drawing.Color.FromArgb(0x1B, 0x21, 0x2C);
        private static readonly System.Drawing.Color Muted = System.Drawing.Color.FromArgb(0x6B, 0x74, 0x84);
        private static readonly System.Drawing.Color Bad = System.Drawing.Color.FromArgb(0xB4, 0x23, 0x18);

        private readonly System.Drawing.Icon? _icon;
        private string _status = "Starting…";
        private bool _failed;

        public SplashPanel(System.Drawing.Icon? icon)
        {
            _icon = icon;
            BackColor = Page;
            DoubleBuffered = true;
        }

        /// <summary>Turns the splash into an explanation instead of a wait.</summary>
        public void ShowFailure(string message)
        {
            _failed = true;
            _status = "MapleBench could not start its window.\n\n" + message +
                      "\n\nOpening the UI in your browser instead.";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            System.Drawing.Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int cx = Width / 2;
            int cy = Height / 2;

            if (_icon is not null && !_failed)
            {
                try { g.DrawIcon(new System.Drawing.Icon(_icon, 64, 64), new System.Drawing.Rectangle(cx - 32, cy - 96, 64, 64)); }
                catch { /* the wordmark alone is enough */ }
            }

            using System.Drawing.Font title = new("Segoe UI Semibold", 20f);
            using System.Drawing.Font body = new("Segoe UI", 10f);
            using System.Drawing.StringFormat centre = new()
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Near,
            };

            using System.Drawing.SolidBrush ink = new(Ink);
            using System.Drawing.SolidBrush note = new(_failed ? Bad : Muted);

            g.DrawString("MapleBench", title, ink,
                new System.Drawing.RectangleF(0, cy - 20, Width, 40), centre);
            g.DrawString(_status, body, note,
                new System.Drawing.RectangleF(cx - 260, cy + 24, 520, 160), centre);
        }
    }
}
