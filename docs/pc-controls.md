# Mouse and keyboard on Windows

Use a build with the PC input profile and select the title's **Default** controller scheme.
Start it with `scripts\play.ps1 -Exe <title.exe> -PcControls`. For a direct run or
the portable launcher, set `$env:VICTORY_PC_CONTROLS = '1'` in PowerShell first.

The profile starts on foot. Press **F5** upon entering or leaving a vehicle to
switch the layout. **F6** releases or captures the mouse. Switching windows
releases the cursor. A connected physical controller also feeds the virtual pad.

| Xbox prompt | On foot | Driving or boating |
| --- | --- | --- |
| Left stick direction (L) | WASD or numpad 8/4/2/6 | A/D steer. Numpad 8/4/2/6 moves the full stick |
| Left stick press (LS/L3) | Shift | Shift or E |
| Right stick direction (R) | Move the mouse or use I/J/K/L | Same |
| Right stick press (RS/R3) | C or H | C |
| LB / RB | Tab / Q | Tab or right mouse / Q or left mouse |
| LT / RT | Right mouse or Z / left mouse | S or Z for brake or reverse / W for throttle |
| A | Space or Enter | Same |
| B | Ctrl, Backspace or B | Ctrl, Backspace, B or H |
| X | R or X | Same |
| Y | E, F or Y | F or Y |
| D-pad | Arrow keys | Same |
| View (two overlapping squares) | M | M |
| Menu (three lines) | Esc | Esc |

The game chooses the action for each button. A **View** prompt labeled **Reset**
uses **M**. Hold a key when the title asks for a hold. For a skippable scene
with an **A** prompt, hold Space. Turn Num Lock on for the numpad directions.
For simultaneous stick presses use Shift+C. For both shoulder buttons use Tab+Q.
