# Windows runtime monitor

Run the title and open a second Windows PowerShell window. From the source checkout run

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\watch.ps1
```

The monitor attaches to the active VIctory process. To select one of several runs,
supply `-ProcessId 1234`. The sidebar follows the game window, passes mouse
clicks through and hides when the game is minimized. On builds advertising
the PC input profile it also displays both keyboard layouts, stick directions and
presses, triggers and shoulders. The selected layout and held inputs light up.
Use `-NoPanel` to record just the data.

Each recording gets a new directory under `%TEMP%\VIctory-monitor`. Set
`-OutputRoot` to keep it elsewhere. Stop the recording with Ctrl+C or by
closing the game. Windows PowerShell 5.1 and the built-in .NET Framework supply the tools.

| File | Measures |
| --- | --- |
| `hardware.json` | CPU model, visible RAM, most recently active game 3D adapter and its Windows LUID |
| `frames.csv` | The FPS, frame time and draws per frame reported in the title at each poll |
| `samples.csv` | Per-process CPU, working set, private bytes, GPU 3D utilization, dedicated/shared GPU memory and system RAM use |

The game updates its title periodically, so consecutive polls can repeat a frame statistic.
CPU is a percentage of the machine's logical processors. GPU utilization is the
busiest 3D engine for that PID on the selected adapter. Missing counters appear
as blank values in the CSV and an ellipsis on screen.
