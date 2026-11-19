using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// Select text with the mouse anywhere – the island offers translate · rephrase · explain by itself, no hotkey
/// (user wish 2026-11-19). The selection is read through Windows' accessibility interface (UI Automation): nothing is
/// copied, no key is pressed. Apps that don't tell their selection that way simply don't show the menu (Ctrl+Alt+A
/// still works there).
/// </summary>
public partial class IslandWindow
{
    private readonly DispatcherTimer _selWatch = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _selDown;
    private Native.POINT _selFrom;
    private DateTime _selUpAt;
    private Native.POINT _selUpPos;
    private string _selLast;
    private DateTime _selLastAt;
    private bool _selBusy;

    private void WireSelectionMenu()
    {
        _selWatch.Tick += (_, _) => SelectionTick();
        _selWatch.Start();
    }

    private void SelectionTick()
    {
        if (!S.SelectionMenu) return;
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        Native.GetCursorPos(out var p);
        if (down && !_selDown) { _selDown = true; _selFrom = p; return; }
        if (down || !_selDown) return;
        _selDown = false;
        // a drag over text, or a double-click on a word
        double dist = Math.Abs(p.X - _selFrom.X) + Math.Abs(p.Y - _selFrom.Y);
        bool dbl = DateTime.Now - _selUpAt < TimeSpan.FromMilliseconds(450) && Math.Abs(p.X - _selUpPos.X) + Math.Abs(p.Y - _selUpPos.Y) < 6;
        _selUpAt = DateTime.Now;
        _selUpPos = p;
        if (dist < 14 && !dbl) return;
        if (_selBusy || _state != State.Collapsed || IsCursorOverIsland(10) || !AiService.HasKey(S.AiProvider)) return;
        if (Native.GetForegroundWindow() is var fg && fg != IntPtr.Zero && Native.GetWindowThreadProcessId(fg, out var pid) != 0 && pid == Environment.ProcessId) return;
        _selBusy = true;
        Task.Run(async () =>
        {
            await Task.Delay(140); // the app first finishes its selection
            string text = null;
            try
            {
                var el = AutomationElement.FocusedElement;
                if (el != null && el.TryGetCurrentPattern(TextPattern.Pattern, out var pat))
                {
                    var ranges = ((TextPattern)pat).GetSelection();
                    text = string.Join(" ", ranges.Select(r => r.GetText(4000))).Trim();
                }
            }
            catch { }
            Ui.Run(() =>
            {
                _selBusy = false;
                if (string.IsNullOrWhiteSpace(text) || text.Length < 2 || text.Length > 4000) return;
                if (text == _selLast && DateTime.Now - _selLastAt < TimeSpan.FromSeconds(10)) return;
                _selLast = text;
                _selLastAt = DateTime.Now;
                if (_state == State.Collapsed) SelectionMenu(text, fromHotkey: false);
            });
        });
    }
}
