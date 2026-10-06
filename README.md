# Window Resizer

A Windows 10/11 desktop application built with C#/.NET 8, ImGui.NET, Veldrid, and Win32 P/Invoke. It follows the supplied `app_details.txt` and PNG layout references.

`WindowResizer.ico` is embedded in the executable and used for the running window and taskbar icon.

## Run

From this folder:

```powershell
dotnet run --project .\WindowResizer\WindowResizer.csproj
```

The first launch creates `application_settings.json` beside the executable with seven default resolutions, Window mode, and a light theme. To run the functional checks:

```powershell
dotnet run --project .\WindowResizer.Checks\WindowResizer.Checks.csproj
```

The checks cover saved settings, ordered resolutions, duplicate removal, an intentionally empty list, and exact outer/client resizing on a temporary Windows form.

## Use

Select a window and a resolution, choose **Window** or **Content Area**, then click **Set Resolution**. Double-clicking a resolution runs the same command. Double-clicking a window attempts to bring it to the foreground. Filter searches process names and window titles; the brush clears it and refresh enumerates again. The Diagnostics panel reports Win32 results and actual dimensions.

Click the moon or sun icon at the bottom right to switch between light and dark themes. The choice is saved in `application_settings.json` and restored on startup.

Windows may restrict focus changes or enforce target window sizes. The app reports those outcomes in Diagnostics. It runs with normal user privileges.
