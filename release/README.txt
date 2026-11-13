PORT VISUALIZER 1.0.0
See what your computer is communicating with.

WHAT IT IS
A defensive Windows network visibility tool. It lists active TCP connections and UDP endpoints (IPv4/IPv6), shows the owning process, draws an interactive network map, keeps a local history and lets you define alert rules. It does not capture packets, read traffic content, collect credentials, kill processes, change the firewall or upload anything.

FILES
  PortVisualizer.exe        Portable single-file app (Windows 10/11 x64, no .NET install needed)
  PortVisualizer_Setup.exe  Installer (Start Menu shortcut, optional desktop shortcut, uninstaller)

INSTALL
  Run PortVisualizer_Setup.exe. It installs per user by default (no administrator rights);
  choose "all users" in the dialog for Program Files. Running a newer setup upgrades in place and
  keeps your data. On uninstall you are asked whether to delete your data.
  Or just run PortVisualizer.exe from anywhere.

DATA LOCATION
  %LOCALAPPDATA%\PortVisualizer
    portvisualizer.db   settings, history, alert rules (SQLite)
    logs\               rolling log files
    bootstrap.json      only if you move the database

PERMISSIONS
  Normal user rights are enough for all monitoring. Windows hides the executable path of some protected
  processes from non-administrators; those appear by name only. Settings > Restart as administrator is
  optional. The app never starts with Windows by itself.

KNOWN LIMITATIONS
  - UDP has no remote endpoint or state in the Windows tables; none is shown.
  - "No embedded signature" may still be a catalog-signed Windows file (catalog check not implemented).
  - Bandwidth is not measured and never displayed.
  - Graph is capped at 4000 remote nodes for responsiveness.
  - The single-file build unpacks its WPF native libraries to %TEMP%\.net on first start.

LICENSE
  MIT, see LICENSE.txt.
