@echo off
rem Builds ScreenLayout.exe from src\*.cs with the C# compiler that ships with Windows (.NET Framework 4).
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  /out:"%~dp0ScreenLayout.exe" /recurse:"%~dp0src\*.cs"
