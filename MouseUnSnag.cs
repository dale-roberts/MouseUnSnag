/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Dale Roberts. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

using static StaticStuff;
using static SnagScreen;

public static class StaticStuff
{
    // ============================================================================================
    // Win32 interfaces.
    //

    public const int WH_MOUSE_LL = 14; // Win32 low-level mouse event hook ID.
    public const int WM_MOUSEMOVE = 0x0200;

    public delegate IntPtr HookProc (int nCode, uint wParam, IntPtr lParam);

    [DllImport ("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx (int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport ("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [
        return :MarshalAs (UnmanagedType.Bool)
    ]
    public static extern bool UnhookWindowsHookEx (IntPtr hhk);

    [DllImport ("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr CallNextHookEx (IntPtr hhk, int nCode, uint wParam, IntPtr lParam);

    [DllImport ("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle (string lpModuleName);

    [DllImport ("user32.dll")]
    public static extern bool SetCursorPos (int X, int Y);
    public static bool SetCursorPos (Point p) { return SetCursorPos (p.X, p.Y); }

    [DllImport ("user32.dll")]
    public static extern bool GetCursorPos (out Point lpPoint);

    public delegate bool ConsoleEventDelegate (int eventType);

    [DllImport ("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler (ConsoleEventDelegate callback, bool add);

    public enum PROCESS_DPI_AWARENESS
    {
        Process_DPI_Unaware = 0,
        Process_System_DPI_Aware = 1,
        Process_Per_Monitor_DPI_Aware = 2
    }

    [DllImport ("SHCore.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwareness (PROCESS_DPI_AWARENESS awareness);

    [StructLayout (LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public enum DpiType
    {
        Effective = 0,
        Angular = 1,
        Raw = 2
    }

    //https://msdn.microsoft.com/en-us/library/windows/desktop/dd145062(v=vs.85).aspx
    [DllImport ("User32.dll")]
    public static extern IntPtr MonitorFromPoint ([In] Point pt, [In] uint dwFlags);

    //https://msdn.microsoft.com/en-us/library/windows/desktop/dn280510(v=vs.85).aspx
    [DllImport ("Shcore.dll")]
    public static extern IntPtr GetDpiForMonitor ([In] IntPtr hmonitor, [In] DpiType dpiType, [Out] out uint dpiX, [Out] out uint dpiY);

    public static uint GetDpi (Screen screen, DpiType dpiType)
    {
        try
        {
            var mon = MonitorFromPoint (screen.Bounds.Location, 2 /*MONITOR_DEFAULTTONEAREST*/ );
            GetDpiForMonitor (mon, dpiType, out uint dpiX, out uint dpiY);
            return dpiX;
        }
        catch (DllNotFoundException)
        {
            return 96; // On Windows <8, just assume scaling 100%.
        }
    }

    // ============================================================================================
    // ============================================================================================
    // Geometric helpers. These all deal with Rectangles, Points, and X and Y values.
    //

    // Return the signs of X and Y. This essentially gives us the "component direction" of
    // the point (N.B. the vector length is not "normalized" to a length 1 "unit vector" if
    // both the X and Y components are non-zero).
    public static Point Sign (Point p) => new Point (Math.Sign (p.X), Math.Sign (p.Y));

    // "Direction" vector from P1 to P2. X/Y of returned point will have values
    // of -1, 0, or 1 only (vector is not normalized to length 1).
    public static Point Direction (Point P1, Point P2) => Sign (P2 - (Size) P1);

    // If P is anywhere inside R, then OutsideDistance() returns (0,0).
    // Otherwise, it returns the (x,y) delta (sign is preserved) from P to the
    // nearest edge/corner of R. For Right and Bottom we must correct by 1,
    // since the Rectangle Right and Bottom are one larger than the largest
    // valid pixel.
    public static int OutsideXDistance(Rectangle R, Point P)
        => Math.Max (Math.Min (0, P.X - R.Left), P.X - R.Right + 1);
        
    public static int OutsideYDistance(Rectangle R, Point P)
        => Math.Max (Math.Min (0, P.Y - R.Top), P.Y - R.Bottom + 1);
        
    public static Point OutsideDistance (Rectangle R, Point P)
        => new Point (OutsideXDistance(R,P), OutsideYDistance(R,P));

    // This is sort-of the "opposite" of above. In a sense it "captures" the point to the
    // boundary/inside of the rectangle, rather than "excluding" it to the exterior of the rectangle.
    //
    // If the point is outside the rectangle, then it returns the closest location on the
    // rectangle boundary to the Point. If Point is inside Rectangle, then it just returns
    // the point.
    public static Point ClosestBoundaryPoint (this Rectangle R, Point P)
        => new Point (
            Math.Max (Math.Min (P.X, R.Right - 1), R.Left),
            Math.Max (Math.Min (P.Y, R.Bottom - 1), R.Top));

    // In which direction(s) is(are) the point outside of the rectangle? If P is
    // inside R, then this returns (0,0). Else X and/or Y can be either -1 or
    // +1, depending on which direction P is outside R.
    public static Point OutsideDirection (Rectangle R, Point P) => Sign (OutsideDistance (R, P));
}

// =======================================================================================================
// =======================================================================================================

// SnagScreen keeps track of the physical screens/monitors attached to the system, and adds
// some members to keep track of screen relative geometry.
//
// N.B. (Note Well!) There is a non-static "instance" of SnagScreen for EACH physical
// screen/monitor in the system. The *static* members are relative to ALL the screens/monitors.
public class SnagScreen
{
    public static SnagScreen[] All;
    public static List<SnagScreen> LeftMost, RightMost, TopMost, BottomMost;
    public static Rectangle BoundingBox;  // Rectangle that contains all screens.

    public Screen screen; // Points to the entry in Screen.AllScreens[].
    public int Num; // Index into Screen.AllScreens[] for this SnagScreen object.
    public Rectangle R => screen.Bounds; // Shortcut to screen.Bounds.
    public override string ToString() => Num.ToString();

    public List<SnagScreen> ToLeft, ToRight, Above, Below;
    public static string AsString(List<SnagScreen> L) => string.Join (",", L.Select (sn => sn.Num));

    // Initialize each SnagScreen from each member of Screen.AllScreens[] array.
    public SnagScreen (Screen S, int ScreenNum)
    {
        screen = S;
        Num = ScreenNum;
        ToLeft = new List<SnagScreen> ();
        ToRight = new List<SnagScreen> ();
        Above = new List<SnagScreen> ();
        Below = new List<SnagScreen> ();
    }

    public bool IsLeftmost => ToLeft.Count == 0;
    public bool IsRightmost => ToRight.Count == 0;
    public bool IsTopmost => Above.Count == 0;
    public bool IsBottommost => Below.Count == 0;

    public bool OverlapX (Rectangle R1, Rectangle R2) => (R1.Left < R2.Right) && (R1.Right > R2.Left);
    public bool OverlapY (Rectangle R1, Rectangle R2) => (R1.Top < R2.Bottom) && (R1.Bottom > R2.Top);

    // If s is immediately adjacent to (shares a border with) us, then add it to the
    // appropriate direction list. If s is not "touching" us, then it will not get added to
    // any list. s can be added to at most one list (hence use of "else if" instead of just
    // a sequence of "if's").
    public void AddDirectionTo (SnagScreen s)
    {
        if ((R.Right == s.R.Left) && OverlapY (R, s.R)) ToRight.Add (s);
        else if ((R.Left == s.R.Right) && OverlapY (R, s.R)) ToLeft.Add (s);
        else if ((R.Top == s.R.Bottom) && OverlapX (R, s.R)) Above.Add (s);
        else if ((R.Bottom == s.R.Top) && OverlapX (R, s.R)) Below.Add (s);
    }

    // Loop through Screen.AllScreens[] array to initialize ourselves.
    public static void Init (Screen[] AllScreens)
    {
        var N = AllScreens.Length;
        TopMost = new List<SnagScreen> ();
        BottomMost = new List<SnagScreen> ();
        LeftMost = new List<SnagScreen> ();
        RightMost = new List<SnagScreen> ();

        BoundingBox = new Rectangle(0,0,0,0);

        // First pass, populate our All[] array with all the screens.
        All = new SnagScreen[N];
        for (int i = 0; i < N; ++i)
            All[i]  = new SnagScreen (Screen.AllScreens[i], i);

        // Now determine their geometric relationships. Yes this is O(N^2), but
        // usually N (number of monitors) is not too large. There may be more
        // efficient approaches, but this is very simple, clear, and
        // straightforward, and it is not called often (only when program
        // starts, and after any change in monitor configuration).
        foreach (var SN in All)
        {
            // Add direction from this SN screen to each of the other screens.
            foreach (var s in All)
                SN.AddDirectionTo (s);

            // Where appropriate, add ourselves to the lists of outermost screens.
            if (SN.IsLeftmost) LeftMost.Add (SN);
            if (SN.IsRightmost) RightMost.Add (SN);
            if (SN.IsTopmost) TopMost.Add (SN);
            if (SN.IsBottommost) BottomMost.Add (SN);

            BoundingBox = Rectangle.Union(BoundingBox, SN.R);
        }
    }

    public static void ShowAll ()
    {
        int N = All.Length;
        Console.WriteLine ($"There {((N>1)?"are":"is")} {N} SCREEN{((N>1)?"S":"")}:");
        int i = 0;

        foreach (var S in All)
        {
            var DPIEffective = GetDpi (S.screen, DpiType.Effective);
            var R = S.R;

            Console.WriteLine (
                $"   {i}: ({R.Left},{R.Top})-({R.Right},{R.Bottom})   Size:({R.Width},{R.Height}) "+
                $"L({AsString(S.ToLeft)}),R({AsString(S.ToRight)}),A({AsString(S.Above)}),B({AsString(S.Below)})    "+
                $"DPI(Raw/Eff/Ang): {GetDpi(S.screen, DpiType.Raw)}/{DPIEffective}/{GetDpi(S.screen, DpiType.Angular)}  "+
                $"Screen Scaling: {Math.Round(DPIEffective/96.0*100)}%   \r"); //  {S.DeviceName}     \r");
            ++i;
        }
        Console.WriteLine($"Rtmost({AsString(RightMost)}), Lfmost({AsString(LeftMost)}), "+
            $"Topmost({AsString(TopMost)}), Btmost({AsString(BottomMost)})   "+
            $"BoundingBox{BoundingBox}");
    }
    
    // Find which screen the point is on. If it is not on one, return null.
    public static SnagScreen WhichScreen (Point P)
    {
        foreach(var S in SnagScreen.All)
            if(S.R.Contains(P))
                return S;

        return null;
    }

    // Find the first monitor (first one we come across in the for() loop)
    // that is in the direction of the point.
    public static SnagScreen ScreenInDirection (Point Dir, Rectangle CurScreen)
    {
        // Screen must be strictly above/below/beside. For instance, for a monitor to be
        // "above", the monitor's Bottom equal to the current screen's Top ("current
        // screen" is where the Cursor (NOT the mouse!!) is currently).
        foreach(var S in SnagScreen.All)
        {
            if (((Dir.X == 1) && (CurScreen.Right == S.R.Left)) ||
                ((Dir.X == -1) && (CurScreen.Left == S.R.Right)) ||
                ((Dir.Y == 1) && (CurScreen.Bottom == S.R.Top)) ||
                ((Dir.Y == -1) && (CurScreen.Top == S.R.Bottom)))

                return S;
        }
        return null;
    }

    // May want to update the above routine, which arbitrarily selects the monitor that
    // happens to come first in the for() loop. We should probably do a little extra work,
    // and select the monitor that is closest to the mouse position.

    // Find the monitor that is closest to the point.
    //public static SnagScreen ScreenInDirection()
    //{
    //}

    // Find the best point to "wrap" around the cursor, either horizontally or
    // vertically. We consider only the "OuterMost" screens. For instance, if
    // the mouse is moving to the left, we consider only the screens in the
    // RightMost[] array.
    public static Point WrapPoint(Point Dir, Point Cursor)
    {
        int DistClosest = int.MaxValue;
        SnagScreen WS = null; // Our "wrap screen".

        if(Dir.X != 0) {
            // Find closest Left- or Right-most screen, in Y direction.
            foreach(var S in (Dir.X==1 ? LeftMost : RightMost)) {
                int dist = Math.Abs(OutsideYDistance(S.R, Cursor));
                if(dist < DistClosest) {
                    DistClosest = dist;
                    WS = S;
                }                
            }
            return WS.R.ClosestBoundaryPoint(new Point(Dir.X==1?WS.R.Left:WS.R.Right, Cursor.Y));
        }

        // We should never get here, but if we do, just return the current
        // Cursor location.
        return Cursor;
    }
}

// =======================================================================================================
// =======================================================================================================
// 
// The MouseUnSnag class deals with the low-level mouse events.
//
//

public class MouseUnSnag
{
    private IntPtr LLMouse_hookhand = IntPtr.Zero;
    private Point LastMouse = new Point (0, 0);
    IntPtr ThisModHandle = IntPtr.Zero;
    int NJumps = 0;

    private IntPtr SetHook (int HookNum, HookProc proc)
    {
        using (Process curProcess = Process.GetCurrentProcess ())
        using (ProcessModule curModule = curProcess.MainModule)
        {
            if (ThisModHandle == IntPtr.Zero)
                ThisModHandle = GetModuleHandle (curModule.ModuleName);
            return SetWindowsHookEx (HookNum, proc, ThisModHandle, 0);
        }
    }

    private void UnsetHook (ref IntPtr hookHand)
    {
        if (hookHand == IntPtr.Zero)
            return;

        UnhookWindowsHookEx (hookHand);
        hookHand = IntPtr.Zero;
    }

    // CheckJumpCursor() returns TRUE, ONLY if the cursor is "stuck". By "stuck" we
    // specifically mean that the user is trying to move the mouse beyond the boundaries of
    // the screen currently containing the cursor. This is determined when the *current*
    // cursor position does not equal the *previous* mouse position. If there is another
    // adjacent screen (or a "wrap around" screen), then we can consider moving the mouse
    // onto that screen.
    //
    // Note that this is ENTIRELY a *GEOMETRIC* method. Screens are "rectangles", and the
    // cursor and mouse are "points." The mouse/cursor hardware interaction (obtaining
    // current mouse and cursor information) is handled in routines further below, and any
    // Screen changes are handled by the DisplaySettingsChanged event. There are no
    // hardware or OS/Win32 references or interactions here.
    bool CheckJumpCursor (Point mouse, Point cursor, out Point NewCursor)
    {
        NewCursor = cursor; // Default is to not move cursor.
			
        // Gather pertinent information about cursor, mouse, screens.
        Point Dir = Direction (cursor, mouse);
        SnagScreen cursorScreen = WhichScreen (cursor);
        SnagScreen mouseScreen = WhichScreen (mouse);
        bool IsStuck = (cursor != LastMouse) && (mouseScreen != cursorScreen);
        Point StuckDirection = OutsideDirection (cursorScreen.R, mouse);

        string StuckString = IsStuck ? "--STUCK--" : "         ";
		
//        Console.Write ($" FarOut{StuckDirection}/{OutsideDis//tance(cursorScreen.R, mouse)} " +
//            $"mouse:{mouse}  cursor:{cursor} (OnMon#{cursorScreen}/{mouseScreen}) last:{LastMouse}  " +
//            $"#UnSnags {NJumps}   {StuckString}        \r");

        Console.Write ($" StuckDirection/Distance{StuckDirection}/{OutsideDistance(cursorScreen.R, mouse)} " +
            $"cur_mouse:{mouse}  prev_mouse:{LastMouse} ==? cursor:{cursor} (OnMon#{cursorScreen}/{mouseScreen})  " +
            $"#UnSnags {NJumps}   {StuckString}   \r");

		LastMouse = mouse;

        // Let caller know we did NOT jump the cursor.
        if (!IsStuck)
            return false;

        SnagScreen jumpScreen = ScreenInDirection (StuckDirection, cursorScreen.R);

        // If the mouse "location" (which can take on a value beyond the current
        // cursor screen) has a value, then it is "within" another valid screen
        // bounds, so just jump to it!
        if (mouseScreen != null)
        {
            NewCursor = mouse;
        }
        else if (jumpScreen != null)
        {
            NewCursor = jumpScreen.R.ClosestBoundaryPoint (cursor);
        }
        else if (StuckDirection.X != 0)
        {
            NewCursor = WrapPoint (StuckDirection, cursor);
        }
        else
            return false;

        ++NJumps;
		Console.Write($"\n -- JUMPED!!! --\n");
        return true;
    }

    // Called whenever the mouse moves. This routine leans entirely on the
    // CheckJumpCursor() routine to see if there is any need to "mess with" the cursor
    // position, to make it jump from one monitor to another.
    private IntPtr LLMouseHookCallback (int nCode, uint wParam, IntPtr lParam)
    {
        if ((nCode < 0) || (wParam != WM_MOUSEMOVE) || UpdatingDisplaySettings)
            goto ExitToNextHook;

        var hookStruct = (MSLLHOOKSTRUCT) Marshal.PtrToStructure (lParam, typeof (MSLLHOOKSTRUCT));
        Point mouse = hookStruct.pt;

        // If we jump the cursor, then we return 1 here to tell the OS that we
        // have handled the message, so it doesn't call SetCursorPos() right
        // after we do, and "undo" our call to SetCursorPos().
        if (GetCursorPos(out Point cursor) && CheckJumpCursor (mouse, cursor, out Point NewCursor)) {
            SetCursorPos(NewCursor);
            return (IntPtr) 1;
        }

        // Default is to let the OS handle the mouse events, when "return" does not happen in
        // if() clause above.
        ExitToNextHook:
            return CallNextHookEx (LLMouse_hookhand, nCode, wParam, lParam);
    }

    bool UpdatingDisplaySettings=false;
    void Event_DisplaySettingsChanged (object sender, EventArgs e)
    {
        UpdatingDisplaySettings=true;
        Console.WriteLine ("\nDisplay Settings Changed...");
        //ShowScreens ();
        SnagScreen.Init (Screen.AllScreens);
        SnagScreen.ShowAll ();
        UpdatingDisplaySettings=false;
    }

    // Need to explicitly keep a reference to this, so it does not get "garbage collected."
    private HookProc MouseHookDelegate = null;

    // Catch program CTRL-C termination, and unhook the mouse event.
    private ConsoleEventDelegate CTRL_C_handler;
    private bool ConsoleEventCallback (int eventType)
    {
        Console.Write ("\nIn ConsoleEventCallback, Unhooking mouse events...");
        UnsetHook (ref LLMouse_hookhand);
        SystemEvents.DisplaySettingsChanged -= Event_DisplaySettingsChanged;
        Console.WriteLine ("  Done.");
        return false;
    }

    private void Run ()
    {
        // DPI Awareness API is not available on older OS's, but they work in
        // physical pixels anyway, so we just ignore if the call fails.
        try
        {
            SetProcessDpiAwareness (PROCESS_DPI_AWARENESS.Process_Per_Monitor_DPI_Aware);
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine ("No SHCore.DLL. No problem.");
        }

        // Make sure we catch CTRL-C hard-exit of program.
        CTRL_C_handler = ConsoleEventCallback;
        SetConsoleCtrlHandler (CTRL_C_handler, true);

        //ShowScreens ();
        SnagScreen.Init (Screen.AllScreens);
        SnagScreen.ShowAll ();

        // Get notified of any screen configuration changes.
        SystemEvents.DisplaySettingsChanged += Event_DisplaySettingsChanged;

        //ShowWindow(GetConsoleWindow(), SW_HIDE);

        // Keep a reference to the delegate, so it does not get garbage collected.
        MouseHookDelegate = LLMouseHookCallback;
        LLMouse_hookhand = SetHook (WH_MOUSE_LL, MouseHookDelegate);

        Console.WriteLine ();

        // This is the one that runs "forever" while the application is alive, and handles
        // events, etc. This application is ABSOLUTELY ENTIRELY driven by the LLMouseHook
        // and DisplaySettingsChanged events.
        Application.Run ();

        Console.WriteLine ("Exiting!!!");
        UnsetHook (ref LLMouse_hookhand);
    }

    public static void Main ()
    {
        // Make sure the MouseUnSnag.exe has only one instance running at a time.
        if ((new Mutex (true, "__MouseUnSnag_EXE__", out bool createdNew) == null) || !createdNew)
        {
            Console.WriteLine ("Already running!! Quitting this instance...");
            return;
        }

        var MUS = new MouseUnSnag ();
        MUS.Run (args);
    }
}
